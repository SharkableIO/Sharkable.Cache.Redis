# Changelog

All notable changes to Sharkable.Cache.Redis are documented here.

## [Unreleased]

### security

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