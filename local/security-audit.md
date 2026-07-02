# Sharkable.Cache.Redis Security Audit Report

**Audit date:** 2026-07-02
**Package version:** 0.5.3
**Scope:** `/Volumes/Doc/dev/Sharkable.Cache.Redis/src/Sharkable.Cache.Redis/`
**Method:** Read-only static analysis — no code modifications
**Companion audit:** `Sharkable` core library at `/Volumes/Doc/dev/Sharkable/local/security-audit.md` (60+ findings, used here for severity grading)

---

## Executive Summary

The Redis plugin package is small (7 source files, ~360 LoC) and follows the same minimal-API idiom as the core library. However, the distributed-lock implementation in `RedisSagaStore` and `RedisCronJobStore` reproduces a well-known Redlock anti-pattern (unconditional `KeyDelete` instead of check-and-delete), and `RedisHealthCheck` reflects internal Redis topology into unauthenticated `/healthz` responses. The Lua script in `RedisRateLimitStore` is correctly parameterized (no injection), and all Redis commands use typed `RedisKey`/`RedisValue` APIs (no string concatenation into commands).

| Severity | Count |
|----------|------:|
| **Critical** | **2** |
| **High** | **5** |
| **Medium** | **7** |
| **Low / Informational** | **8** |

Total: **22** findings.

---

## CRITICAL findings (2)

### C-1 — Distributed locks use unconditional `KeyDelete` (lock-stealing / split-brain saga & cron)

**Files:**
- `src/Sharkable.Cache.Redis/RedisSagaStore.cs:32-36` (ReleaseLockAsync)
- `src/Sharkable.Cache.Redis/RedisSagaStore.cs:49-54` (DeleteAsync)
- `src/Sharkable.Cache.Redis/RedisCronJobStore.cs:35-39` (ReleaseJobLockAsync)

**Severity:** Critical (correctness / split-brain distributed transactions)

```csharp
// RedisSagaStore.ReleaseLockAsync
public Task ReleaseLockAsync(string sagaId)
{
    _db.KeyDelete(_lockPrefix + sagaId);   // <-- deletes whoever owns the key now
    return Task.CompletedTask;
}

// RedisCronJobStore.ReleaseJobLockAsync
public Task ReleaseJobLockAsync(string jobName)
{
    _db.KeyDelete(_lockPrefix + jobName);  // <-- same
    return Task.CompletedTask;
}
```

Both stores acquire locks via `StringSetAsync(key, Environment.MachineName, ttl, When.NotExists)` — i.e. the lock VALUE is `Environment.MachineName` (the fencing token). But on release they never check that the stored value still matches their own token. They simply `DEL` whatever is there.

**Attack scenario:**

A saga for "debit account A, credit account B, send notification" has lock TTL = 5 min (hard-coded `SagaExecutor.LockTtl`, see `Sharkable/src/Sharkable/DistributedTx/SagaExecutor.cs:19`).

1. `T=0` — Instance A acquires lock `sharkable:saga:lock:tx-42`.
2. `T=4:30` — A finishes step 1 (debit A), is mid-way through step 2 (credit B).
3. `T=5:00` — Lock expires.
4. `T=5:01` — Instance B sees lock free, acquires it. Reads progress = 1 (step 1 done). Starts step 2 (credit B) **a second time** → double-credit.
5. `T=5:30` — A finishes step 2, falls into `finally { ReleaseLockAsync }` → `KeyDelete("sharkable:saga:lock:tx-42")` deletes B's lock.
6. `T=5:31` — Instance C acquires the now-free lock, reads progress = 1, runs steps 2 and 3 → triple credit.

For cron: any cron job whose runtime exceeds `LockTtl` (default 10 min in `CronScheduler.cs:87`) executes twice in parallel.

**Remediation:**
Replace the unconditional `KeyDelete` with a check-and-delete Lua script (the canonical Redlock release pattern):
```lua
if redis.call('GET', KEYS[1]) == ARGV[1] then
    return redis.call('DEL', KEYS[1])
else
    return 0
end
```
Or use StackExchange.Redis's built-in `IDatabase.LockTakeAsync` / `LockReleaseAsync` (which already implement this pattern via `LockReleaseScript`). Either way, **never** accept a release request from a process that cannot prove ownership.

### C-2 — Saga `LockTtl` is hard-coded to 5 minutes, likely shorter than real saga work

**File (reference):** `Sharkable/src/Sharkable/DistributedTx/SagaExecutor.cs:19`

**Severity:** Critical (paired with C-1 — even with check-and-delete, locks that expire mid-work break correctness assumptions)

```csharp
public TimeSpan LockTtl { get; set; } = TimeSpan.FromMinutes(5);
```

The saga executor never renews / heartbeats its lock. A 6-minute step (long DB query, external payment gateway call with 5-min SLA + retry, large batch upload) guarantees lock expiry. With C-1, this is automatic split-brain. Even with C-1 fixed, a 6-minute saga step that gets GC-paused or context-switched past the TTL still triggers re-execution by another instance — and the original instance, on completion, will incorrectly advance step state.

**Attack scenario:** A maliciously slow saga payload (e.g. user uploads a 1 GB file in step 2 with 5-step saga) consumes enough wall time to expire the lock. Concurrent execution follows.

**Remediation:**
1. Make `LockTtl` configurable per saga with a default that exceeds the 99.99th-percentile step duration in production.
2. Implement a heartbeat/renewal loop inside `SagaExecutor` that calls `StringSetAsync(_lockPrefix + sagaId, token, newTtl)` while work is in progress.
3. Document the lock-vs-work-duration assumption prominently in `RedisSagaStore` XML doc and `SagaExecutor.LockTtl`.

---

## HIGH findings (5)

### H-1 — `RedisHealthCheck` leaks Redis server topology and connection error details via `/healthz`

**File:** `src/Sharkable.Cache.Redis/RedisHealthCheck.cs:36-49`

**Severity:** High (information disclosure on a typically-unauthenticated endpoint)

```csharp
var endpoints = _multiplexer.GetEndPoints();
return HealthCheckResult.Healthy(
    $"Redis connected ({endpoints.Length} endpoint(s)) in {sw.ElapsedMilliseconds}ms",
    new Dictionary<string, object>
    {
        ["latencyMs"] = sw.ElapsedMilliseconds,
        ["endpoints"] = endpoints.Select(e => e.ToString()).ToArray(),  // <-- LEAKS
    });
...
return HealthCheckResult.Unhealthy(
    $"Redis connection failed: {ex.Message}");  // <-- ex.Message leaks addresses
```

`HealthCheckResult.Data` and `Description` are serialized as JSON to `/healthz`. Kubernetes and most reverse-proxy health probes hit this path unauthenticated. Two leaks:

1. `endpoints.Select(e => e.ToString())` exposes the full Redis server topology: `10.42.1.7:6379`, `redis-sentinel-0.redis.svc:26379`, cluster slots, etc. An attacker scanning the app's `/healthz` learns internal-network Redis addresses — a reconnaissance goldmine for lateral movement.
2. `ex.Message` from a failed `PingAsync()` typically includes `It was not possible to connect to the redis server(s); to create a disconnected multiplexer, disable AbortOnConnectFail. 10.42.1.7:6379`. Same leak, plus auth failures leak partial credentials (`WRONGPASS invalid username-password pair`).

Mirrors core finding **M-4** (`JwtHealthCheck` reflects `authority` URL and exception messages).

**Remediation:**
- Replace `endpoints.Select(e => e.ToString())` with a count: `["endpointCount"] = endpoints.Length`.
- Replace `$"Redis connection failed: {ex.Message}"` with a static string: `"Redis connection failed"`.
- If detailed errors must be retained for ops, gate behind a `RedisHealthCheckOptions.IncludeDetails` flag that defaults to `false` and is auto-disabled in production (`!IHostEnvironment.IsDevelopment()`).

### H-2 — `AddSharkableRedis` accepts connection strings without enforcing TLS, `abortConnect=false`, or warning on plaintext

**File:** `src/Sharkable.Cache.Redis/SharkableRedisExtensions.cs:19-26, 61-68`

**Severity:** High (misconfiguration → credential exposure in plaintext, startup DoS)

```csharp
public static IServiceCollection AddSharkableRedis(
    this IServiceCollection services,
    string connectionString,
    Action<RedisStoreOptions>? configure = null)
{
    var multiplexer = ConnectionMultiplexer.Connect(connectionString);  // <-- no validation
    return services.AddSharkableRedis(multiplexer, configure);
}
```

Three problems:

1. **Plaintext by default.** `redis://localhost:6379` or `localhost:6379,password=secret` work without any TLS enforcement. If the connection traverses an untrusted network, the password is on the wire in cleartext. There is no `ssl=true` requirement.
2. **Startup DoS.** `ConnectionMultiplexer.Connect(...)` defaults to `abortConnect=true`. If Redis is unreachable at app startup, the multiplexer throws and the entire application fails to start. An attacker who can drop traffic to the Redis port (or the Redis instance itself) forces a rolling restart cascade.
3. **No password-scrubbing warning.** Developers commonly log connection strings or include them in error reports. `password=secret` is plainly visible. No warning is logged at registration time.

**Remediation:**
- If `connectionString` is used (line 24), parse it into `ConfigurationOptions` and:
  - Set `AbortOnConnectFail = false` if not explicitly set.
  - Require `Ssl = true` if not explicitly set, **or** log a startup warning when plaintext is detected.
- Wrap the startup-time `Connect` in a `try/catch` that produces a clear, actionable error (don't crash the host silently).
- Document that the connection string should be loaded from a secret store, not committed to `appsettings.json`.

### H-3 — `RedisIdempotencyStore.GetAsync` silently swallows deserialization exceptions

**File:** `src/Sharkable.Cache.Redis/RedisIdempotencyStore.cs:50-58`

**Severity:** High (silent failure → potential duplicate side effects)

```csharp
try
{
    var record = JsonSerializer.Deserialize<IdempotencyRecord>((string)value!, JsonOptions);
    return record is not null ? new IdempotencyHit(record) : null;
}
catch
{
    return null;   // <-- all exceptions silently swallowed
}
```

A non-`null` Redis value that fails JSON deserialization returns `null` to the caller. In the middleware (`Sharkable/src/Sharkable/Middleware/Idempotency/SharkIdempotencyMiddleware.cs:62`), a `null` lookup after `TryReserveAsync` returning `false` falls through to "execute the request again" (line 82-85, the race-fallthrough branch).

Likely corruption paths:
1. Schema migration that adds/removes required fields → `JsonException`.
2. Truncation by Redis `allkeys-lru` eviction under memory pressure → `JsonException`.
3. Manual `SET` / `HSET` to the same key namespace with non-record data (e.g., a developer testing in prod).
4. Wire-protocol corruption from a misbehaving proxy.

For a non-idempotent downstream (e.g. payment), the request is **executed twice** with no error log. The middleware even calls `_options.IsValidKey(key)` upfront — there's a clear pattern of validating inputs, but no matching pattern of validating outputs.

**Remediation:**
- Replace `catch { return null; }` with `catch (JsonException ex) { _logger.LogError(ex, "Corrupted idempotency record at key {Key}", key); throw; }`.
- Or, at minimum, propagate the exception via a typed `IdempotencyCorrupted` lookup variant that the middleware treats as a 500 (not a fall-through).
- Inject `ILogger<RedisIdempotencyStore>` for the log line.

### H-4 — `AddSharkableRedis` registers `IHealthCheck` with no name and no opt-out

**File:** `src/Sharkable.Cache.Redis/SharkableRedisExtensions.cs:47`

**Severity:** High (unauthenticated `/healthz` exposed by default)

```csharp
services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck, RedisHealthCheck>();
```

This registers the health check **but does not call `AddHealthChecks().AddCheck(...)`**. Whether the check actually surfaces on `/healthz` depends on the host application calling `services.AddHealthChecks()` separately. In most Sharkable applications that call `AddShark()`, the health endpoint is exposed.

Combined with H-1, `/healthz` is unauthenticated and leaks Redis topology. There is no `AddSharkableRedis(includeHealthCheck: false)` opt-out for operators who want Redis checks for ops dashboards but not public probes.

**Remediation:**
- Add `bool includeHealthCheck = true` parameter to `AddSharkableRedis`.
- Or, gate the registration on `services.AddHealthChecks()` being already present (check via `IHealthChecksBuilder`).
- Document the public-by-default behavior in XML doc.

### H-5 — Connection string passed to `ConnectionMultiplexer.Connect` is not validated for empty / null

**File:** `src/Sharkable.Cache.Redis/SharkableRedisExtensions.cs:19-26`

**Severity:** High (null-ref / crash on misconfiguration)

```csharp
public static IServiceCollection AddSharkableRedis(
    this IServiceCollection services,
    string connectionString,
    Action<RedisStoreOptions>? configure = null)
{
    var multiplexer = ConnectionMultiplexer.Connect(connectionString);  // <-- no null check
    return services.AddSharkableRedis(multiplexer, configure);
}
```

`string` parameter has no null check. `ConnectionMultiplexer.Connect(null)` throws `ArgumentNullException`. In a DI host startup context, this is a hard crash with no actionable error message. A configuration mistake (missing env var, missing `appsettings.json` key) becomes a deployment-failure outage.

**Remediation:**
- Add `if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Redis connection string is required.", nameof(connectionString));` at the top of the method.
- Same for `ConfigurationOptions configuration` overload.

---

## MEDIUM findings (7)

### M-1 — Saga progress records have no TTL (unbounded growth on host crashes)

**File:** `src/Sharkable.Cache.Redis/RedisSagaStore.cs:38-41`

```csharp
public async Task SaveProgressAsync(string sagaId, int stepIndex, CancellationToken ct)
{
    await _db.StringSetAsync(_progressPrefix + sagaId, stepIndex);  // <-- no TTL!
}
```

`saga:progress:<sagaId>` keys persist forever unless `DeleteAsync` is called at saga completion. If the host crashes after step 1 of 5 (lock TTL expires, no recovery instance picks it up — e.g., the saga is application-triggered and never re-invoked), the progress record is orphaned forever. Each crashed saga = 1 permanent Redis entry.

For an attacker who can trigger sagas (the saga ID is developer-controlled, but if the saga is user-triggered, the user picks the ID), this is a slow DoS — fill Redis with orphaned progress records.

**Remediation:** Pass the saga lock TTL to `SaveProgressAsync` (or use a fixed `TimeSpan.FromHours(24)`): `await _db.StringSetAsync(_progressPrefix + sagaId, stepIndex, ttl);`. The lock TTL bounds the orphan window.

### M-2 — Cron state hash entries have no TTL (unbounded growth proportional to registered jobs)

**File:** `src/Sharkable.Cache.Redis/RedisCronJobStore.cs:41-45`

```csharp
public async Task SaveStateAsync(string jobName, CronJobState state)
{
    var json = JsonSerializer.Serialize(state, JsonOptions);
    await _db.HashSetAsync(_stateKey, jobName, json);  // <-- no TTL on hash field
}
```

Each registered cron job adds a hash field to `_stateKey` (`sharkable:cron:states`). Over months of operation with churn (jobs added/removed via deployments), the hash grows. `HashGetAllAsync` (`ListStatesAsync` line 57) reads the entire hash on every cron admin listing. For a host with 1000 registered jobs, this is 1000 JSON deserializations per admin poll.

**Remediation:** Add `HashFieldExpireAsync` (Redis 7.4+) or implement a periodic `HDEL` of fields older than N days. At minimum, document that `sharkable:cron:states` is append-only.

### M-3 — Lua script for rate-limit window has zero-second / negative-second edge case

**File:** `src/Sharkable.Cache.Redis/RedisRateLimitStore.cs:33-39`

```csharp
var ttlSeconds = (long)Math.Ceiling(window.TotalSeconds);
var result = await _db.ScriptEvaluateAsync(
    IncrementScript, new[] { redisKey }, new RedisValue[] { ttlSeconds });
```

If a developer configures `DefaultWindow = TimeSpan.Zero` or `TimeSpan.FromMilliseconds(-100)`, the Lua script receives `0` (or a negative long). `EXPIRE key 0` immediately deletes the key — so the very first `INCR` is never persisted, and every subsequent `INCR` returns 1 (counter never grows past 1 because the key is deleted every cycle). The rate limit becomes useless.

If `window.TotalSeconds < 0` and `Math.Ceiling(-0.5) = 0`, same effect. If `Math.Ceiling(-1.5) = -1`, `EXPIRE key -1` deletes immediately (per Redis docs).

**Remediation:** Reject `window.TotalSeconds <= 0` in `IncrementAsync`:
```csharp
if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
```

### M-4 — `JsonSerializer` without `JsonSerializerContext` — not AOT-safe

**Files:**
- `src/Sharkable.Cache.Redis/RedisIdempotencyStore.cs:16-19, 52, 64`
- `src/Sharkable.Cache.Redis/RedisCronJobStore.cs:12-16, 43, 51, 60`

**Severity:** Medium (defense-in-depth; AOT compatibility)

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

var record = JsonSerializer.Deserialize<IdempotencyRecord>((string)value!, JsonOptions);
var json = JsonSerializer.Serialize(state, JsonOptions);
```

`JsonSerializer.Serialize<T>` and `Deserialize<T>` use runtime reflection on `T`. Under `PublishAot=true` (which the core library's `AotSample` and `NativeTest` projects use), reflection on a non-source-generated type throws at runtime.

The `.csproj` here doesn't set `PublishAot=true`, but if a consumer publishes an AOT app that references this package, the `RedisIdempotencyStore` and `RedisCronJobStore` will fail at the first serialization.

**Remediation:** Add a `JsonSerializerContext` for each store:
```csharp
[JsonSerializable(typeof(IdempotencyRecord))]
[JsonSerializable(typeof(CronJobState))]
public partial class SharkableRedisJsonContext : JsonSerializerContext { }
```
Then pass `SharkableRedisJsonContext.Default.IdempotencyRecord` to `Serialize`/`Deserialize`.

### M-5 — Synchronous `KeyDelete` in async release path (thread-pool blocking)

**Files:**
- `src/Sharkable.Cache.Redis/RedisSagaStore.cs:34, 51-52`
- `src/Sharkable.Cache.Redis/RedisCronJobStore.cs:37`

**Severity:** Medium (thread-pool starvation under contention)

```csharp
public Task ReleaseLockAsync(string sagaId)
{
    _db.KeyDelete(_lockPrefix + sagaId);   // <-- sync StackExchange.Redis call
    return Task.CompletedTask;
}
```

StackExchange.Redis is designed async-first. `IDatabase.KeyDelete` is a synchronous API that may block the calling thread waiting for the multiplexer to dispatch the command. Under contention (1000 concurrent sagas all finishing around the same time), 1000 thread-pool threads can stall on the sync wrapper.

**Remediation:** Use `KeyDeleteAsync` and `await` it. For `DeleteAsync`, `await Task.WhenAll(_db.KeyDeleteAsync(...), _db.KeyDeleteAsync(...))`.

### M-6 — `RedisStoreOptions` prefix properties not validated (cross-tenant collision risk)

**File:** `src/Sharkable.Cache.Redis/RedisStoreOptions.cs:9-25`

**Severity:** Medium (multi-tenant apps: empty prefix → key collision)

```csharp
public string IdempotencyKeyPrefix { get; set; } = "sharkable:idempotency:";
public string SagaLockPrefix { get; set; } = "sharkable:saga:lock:";
// ... 4 more prefix properties, all freely settable
```

A developer can set `SagaLockPrefix = ""` for "simplicity", producing bare `sagaId` keys. If two applications share a Redis instance (common in dev/staging), one application's saga IDs collide with another's. Worse, if a multi-tenant Sharkable app forgets to include the tenant ID in the prefix, tenant A can acquire tenant B's saga lock.

There is no startup validation that the prefixes are non-empty, distinct from each other, or contain a tenant separator.

**Remediation:**
- Reject empty prefixes in `AddSharkableRedis` (or in each store's constructor).
- Optionally validate prefixes match `^[a-zA-Z0-9_\-:.]+:$` (terminate with `:` to prevent prefix collision like `"sharkable:saga"` vs `"sharkable:saga:lock"`).
- For multi-tenant deployments, document that the prefix **must** include the tenant ID.

### M-7 — `ConnectionMultiplexer.Connect` blocks application startup synchronously

**File:** `src/Sharkable.Cache.Redis/SharkableRedisExtensions.cs:24, 66`

**Severity:** Medium (startup latency / DoS if Redis is slow)

```csharp
var multiplexer = ConnectionMultiplexer.Connect(connectionString);
```

`Connect` performs DNS resolution, TCP connect, optional AUTH, and SELECT DB synchronously. The default `connectTimeout` is 5 s; default `syncTimeout` is 5 s. If Redis is slow/unreachable at startup, the app hangs for up to 5 s during DI registration. Combined with H-2 (no `AbortOnConnectFail=false` enforcement), if Redis is **down**, the app crashes immediately.

**Remediation:** Pass `AbortOnConnectFail = false` to the `ConfigurationOptions` parsed from `connectionString`, then use `ConnectionMultiplexer.Connect` — the multiplexer will reconnect in the background. Document this behavior.

---

## LOW findings (8)

### L-1 — Lock value is `Environment.MachineName` — container hostname collisions

**Files:** `RedisSagaStore.cs:29`, `RedisCronJobStore.cs:33`

Multiple containers in the same pod (sidecar pattern) or k8s pods without `pod.spec.hostname` set share the same `Environment.MachineName` (often the node name or a generated suffix). Two instances with the same machine name cannot distinguish their locks. With C-1 fixed (check-and-delete), this is mitigated — but the lock value should be unique.

**Remediation:** Use `Guid.NewGuid().ToString()` per-instance as the fencing token. Store it as a singleton field on the store. This is also what `IDatabase.LockTakeAsync` does internally.

### L-2 — `RedisHealthCheck.PingAsync` has no timeout

**File:** `src/Sharkable.Cache.Redis/RedisHealthCheck.cs:33`

`PingAsync()` honors `syncTimeout` (default 5 s), so this is bounded — but if a misconfigured `syncTimeout = 0` is passed, `PingAsync` can hang indefinitely. Mirrors core finding **M-15**.

**Remediation:** Wrap `PingAsync` in `Task.WhenAny(pingTask, Task.Delay(timeout, ct))`.

### L-3 — `ScriptEvaluateAsync` re-sends full Lua script on every call (minor bandwidth)

**File:** `src/Sharkable.Cache.Redis/RedisRateLimitStore.cs:37-38`

StackExchange.Redis internally caches scripts by SHA1 hash and uses `EVALSHA` when the server has seen the script. The first call uses `EVAL` (full script + SHA1); subsequent calls use `EVALSHA` (just the SHA1). So bandwidth waste is bounded to one round trip. Listed for completeness.

**Remediation:** None required. If you want to be explicit, pre-load the script: `var prepared = LuaScript.Prepare(IncrementScript); await _db.ScriptEvaluateAsync(prepared, ...);`.

### L-4 — `RedisIdempotencyStore.GetAsync` casts `RedisValue` to `(string)` before JSON parse

**File:** `src/Sharkable.Cache.Redis/RedisIdempotencyStore.cs:52`

```csharp
var record = JsonSerializer.Deserialize<IdempotencyRecord>((string)value!, JsonOptions);
```

The `(string)` cast invokes `RedisValue.op_Implicit(string)` which uses `Encoding.UTF8`. If the stored JSON contains non-UTF8 bytes (unlikely for `System.Text.Json` output, but possible if the value was set by an external client), this throws. The current `catch {}` (see H-3) silently swallows it, but a `FormatException` would still propagate from the `value == InFlightMarker` comparison path before the try block.

**Remediation:** Use `(ReadOnlySpan<byte>)value` with `JsonSerializer.Deserialize` from bytes, or use the implicit `RedisValue.ToString()` overload explicitly.

### L-5 — No `ConfigurationOptions` validation on `Database` field

**File:** `src/Sharkable.Cache.Redis/RedisStoreOptions.cs:28`

```csharp
public int Database { get; set; } = -1;
```

If `Database = 1000` is passed (Redis default has 16 DBs), `multiplexer.GetDatabase(1000)` throws on first command. No startup validation.

**Remediation:** Clamp to `[0, 15]` (or `RedisConfig.MaxDatabases` if exposed).

### L-6 — `RedisSagaStore` / `RedisCronJobStore` lock TTL not exposed for configuration

**File:** `src/Sharkable.Cache.Redis/RedisSagaStore.cs:26-30`

The saga lock TTL is passed as a parameter (good), but `RedisSagaStore` doesn't expose defaults or sanity-check the passed `ttl`. If a caller passes `TimeSpan.Zero`, `SET key value 0 NX` returns false (per Redis docs, EX 0 = immediate delete, so SET NX EX 0 always fails — i.e., lock is never acquired).

**Remediation:** Validate `ttl > TimeSpan.Zero` in `TryAcquireLockAsync` and `TryAcquireJobLockAsync`.

### L-7 — `RedisStoreOptions.CronStateKey` defaults to a single key — multi-tenant apps can't shard

**File:** `src/Sharkable.Cache.Redis/RedisStoreOptions.cs:25`

A single `_stateKey` for all cron state is fine for single-tenant apps but couples all tenants on a shared Redis. The default value `"sharkable:cron:states"` doesn't include any tenant ID slot.

**Remediation:** Document the multi-tenant limitation; consider adding `CronStateKeyPrefix` and a per-tenant suffix.

### L-8 — `IConnectionMultiplexer` registered as singleton with no cleanup on host shutdown

**File:** `src/Sharkable.Cache.Redis/SharkableRedisExtensions.cs:43`

`services.AddSingleton(multiplexer);` — when the host shuts down, the multiplexer is disposed via DI container teardown. But `ConnectionMultiplexer` is `IAsyncDisposable`, and DI's default `Singleton` disposal uses sync `Dispose`. For graceful shutdown, the multiplexer should be disposed asynchronously to drain pending commands.

**Remediation:** Register with `services.AddSingleton<IConnectionMultiplexer>(sp => multiplexer);` and inject `IHostApplicationLifetime` to call `DisposeAsync` on `ApplicationStopping`. Or document the limitation.

---

## TOP-10 REMEDIATION PRIORITY

1. **C-1** (Critical) — Replace unconditional `KeyDelete` in `RedisSagaStore.ReleaseLockAsync` and `RedisCronJobStore.ReleaseJobLockAsync` with check-and-delete Lua script (or `IDatabase.LockReleaseAsync`).
2. **C-2** (Critical) — Make saga lock TTL configurable / longer than p99.99 step duration; add a renewal heartbeat to `SagaExecutor`.
3. **H-1** (High) — Strip endpoint topology and `ex.Message` from `RedisHealthCheck` outputs; gate detailed diagnostics behind a config flag.
4. **H-3** (High) — Stop swallowing `JsonException` in `RedisIdempotencyStore.GetAsync`; log and re-throw (or surface as `IdempotencyCorrupted`).
5. **H-2** (High) — Enforce `abortConnect=false` and warn on plaintext in `AddSharkableRedis`; require TLS or log a startup warning.
6. **M-1** (Medium) — Add a TTL to saga progress records so crashed sagas don't leak Redis keys.
7. **H-4** (High) — Add an opt-out for `RedisHealthCheck` registration, or gate on `services.AddHealthChecks()` presence.
8. **M-4** (Medium) — Add `JsonSerializerContext` so the package works under `PublishAot=true`.
9. **M-6** (Medium) — Validate `RedisStoreOptions` prefix values are non-empty, well-formed, and not colliding.
10. **L-1** (Low) — Use a per-instance `Guid` as the lock fencing token instead of `Environment.MachineName`.

---

## File-by-file severity rollup

| File | Critical | High | Medium | Low |
|------|---------:|-----:|-------:|----:|
| `RedisSagaStore.cs` | 1 (shared with cron) | 0 | 2 | 2 |
| `RedisCronJobStore.cs` | 1 (shared with saga) | 0 | 1 | 1 |
| `RedisHealthCheck.cs` | 0 | 1 | 0 | 1 |
| `RedisRateLimitStore.cs` | 0 | 0 | 1 | 1 |
| `RedisIdempotencyStore.cs` | 0 | 1 | 1 | 1 |
| `SharkableRedisExtensions.cs` | 0 | 2 | 1 | 1 |
| `RedisStoreOptions.cs` | 0 | 0 | 1 | 1 |
| `SagaExecutor.cs` (core, referenced) | 1 | 0 | 0 | 0 |

---

## Notes for human review

1. **C-1 and C-2 are linked.** The lack of check-and-delete (C-1) makes the lock-TTL-vs-work-duration assumption (C-2) a guaranteed split-brain trigger. Fixing C-1 alone halves the severity of C-2 from "Critical" to "Medium (operational hazard)".

2. **`SagaExecutor.LockTtl` lives in the core library**, not this package. The fix for C-2 requires editing `Sharkable/src/Sharkable/DistributedTx/SagaExecutor.cs:19`. This audit is read-only for the Redis package, but the recommendation is to coordinate the fix across both repos.

3. **The Lua script in `RedisRateLimitStore` is the cleanest part of the package.** No user input flows into the script body. `KEYS[1]` is a typed `RedisKey`, `ARGV[1]` is a typed `RedisValue` (long). No injection risk in the Lua script itself. This is the model the saga/cron locks should follow.

4. **No reflection-based polymorphic deserialization.** All `JsonSerializer` calls target concrete types (`IdempotencyRecord`, `CronJobState`). Safe from `BinaryFormatter`-style attacks. AOT safety is the only concern (M-4).

5. **No `BinaryFormatter`, `Newtonsoft.Json` with `TypeNameHandling`, or other known-dangerous deserialization patterns** anywhere in the package. Good.

6. **`H-5` (null connection string)** is technically a robustness issue rather than a security one. Listed because in production, a missing config key + a synchronous crash is itself a security-adjacent incident (denial of service to legitimate users).

7. **StackExchange.Redis 2.8.31** is current as of audit. No known unpatched CVEs apply to the API surface used by this package.

8. **`RedisHealthCheck` registration with no name (H-4)** — if the host application later calls `services.AddHealthChecks().AddCheck("redis", ...)`, the duplicate registration may be confusing. Worth checking with the developer who calls `AddSharkableRedis`.

---

**Report end. No code modifications were made.**
