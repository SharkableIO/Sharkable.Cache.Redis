# Changelog

All notable changes to Sharkable.Cache.Redis are documented here.

## [Unreleased]

### security

- Reject non-positive `TimeSpan` rate-limit window in `RedisRateLimitStore.IncrementAsync` — `EXPIRE key 0` would silently delete the counter on the very first call and disable limiting (SHARK-SEC-M03)
- Reject non-positive lock TTL in `RedisSagaStore.TryAcquireLockAsync` and `RedisCronJobStore.TryAcquireJobLockAsync` — `SET NX EX 0` always fails in Redis, blocking a saga/cron job forever (SHARK-SEC-L06)
- Clamp `RedisStoreOptions.Database` to `[-1, 15]` — values like `1000` previously crashed on the first command via `multiplexer.GetDatabase` (SHARK-SEC-L05)
- Validate all six key-prefix properties on `RedisStoreOptions` at `AddSharkableRedis` registration time — reject empty, non-`[A-Za-z0-9:_-]`, or unterminated-with-`:` prefixes that produce cross-tenant collisions (SHARK-SEC-M06)
- Document `RedisStoreOptions.CronStateKey` multi-tenant sharding guidance — the default `sharkable:cron:states` is shared across all tenants on the same Redis instance (SHARK-SEC-L07)
- Own `IConnectionMultiplexer` via new `RedisMultiplexerDisposalService` `IHostedService` — calls `DisposeAsync` on host stop so in-flight commands drain gracefully instead of being force-closed by DI's sync disposal (SHARK-SEC-L08)
- Bound `RedisHealthCheck.PingAsync` with a 3s timeout via `Task.WhenAny` — a misconfigured `syncTimeout=0` could hang the public `/healthz` probe indefinitely (SHARK-SEC-L02)
- Bound cron state hash TTL via new `RedisStoreOptions.CronStateTtl` (default 30d) — `RedisCronJobStore.SaveStateAsync` refreshes `KeyExpireAsync` on every save so deregistered-jobs state eventually expires instead of growing the hash forever (SHARK-SEC-M02)
- Replace `JsonSerializer.Serialize<T>` with source-generated `CacheRedisJsonContext.Default.CronJobState` in `RedisCronJobStore` — Cache.Redis is now fully AOT-compatible (no IL2026/IL3050 from any store's serialization path) (SHARK-SEC-M04)
- Replace unconditional `KeyDelete` in `RedisSagaStore.ReleaseLockAsync` and `RedisCronJobStore.ReleaseJobLockAsync` with check-and-delete Lua script — prevent split-brain when LockTtl expires mid-work (SHARK-SEC-005, cross-repo with `Sharkable`)
- Add `RenewJobLockAsync` to `RedisCronJobStore` + bound `_lockTokens` via `MemoryCache` SizeLimit=10k — prevent split-brain for long-running cron jobs (SHARK-SEC-005)
- Redact `RedisHealthCheck` description — never expose endpoint topology or `ex.Message` on public `/healthz`; full diagnostic detail logged at `LogWarning` for operators only (SHARK-SEC-018)
- Validate connection string at `AddSharkableRedis` — null-check + default `abortConnect=false` + optional TLS enforcement via `RedisStoreOptions.RequireTls` (SHARK-SEC-019)
- Replace empty catch in `RedisIdempotencyStore.GetAsync` with typed exception filter (`JsonException` / `InvalidOperationException`) + structured `LogWarning` + re-throw wrapped in `InvalidOperationException` — prevent silent double-execution when a corrupted record is encountered (SHARK-SEC-020)
- Add `UseSharkableRedisHealthCheck()` extension — explicit opt-in to wire `RedisHealthCheck` into `HealthCheckService` under name `"redis"` and tag `"ready"`. The check is no longer auto-surfaced on `/healthz` by default (SHARK-SEC-021)
- Set TTL on `RedisSagaStore.SaveProgressAsync` via `RedisStoreOptions.SagaProgressTtl` (default 7d) — prevent unbounded Redis key growth from orphaned saga progress records after host crashes (SHARK-SEC-022)
- Replace `JsonSerializer.Serialize<T>` with source-generated `JsonSerializerContext` in `RedisIdempotencyStore` — Cache.Redis is now AOT-compatible (no IL2026/IL3050 from reflective serialization). A local `CacheRedisIdempotencyPayload` wrapper is used because the core `IdempotencyRecord` lives in a separate assembly and is not `partial` (SHARK-SEC-020 follow-up)
- Remove auto-registration of `RedisHealthCheck` as `IHealthCheck` in `AddSharkableRedis` — `UseSharkableRedisHealthCheck()` is now the only way to wire the health check, matching the explicit opt-in pattern (SHARK-SEC-021 follow-up)
- Connection string: only override `abortConnect=false` when the key is absent from the user's string — respect an explicit `abortConnect=true` and stop silently downgrading it (SHARK-SEC-019 follow-up)