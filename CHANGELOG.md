# Changelog

All notable changes to Sharkable.Cache.Redis are documented here.

## [Unreleased]

### security

- Replace unconditional `KeyDelete` in `RedisSagaStore.ReleaseLockAsync` and `RedisCronJobStore.ReleaseJobLockAsync` with check-and-delete Lua script — prevent split-brain when LockTtl expires mid-work (SHARK-SEC-005, cross-repo with `Sharkable`)
- Add `RenewJobLockAsync` to `RedisCronJobStore` + bound `_lockTokens` via `MemoryCache` SizeLimit=10k — prevent split-brain for long-running cron jobs (SHARK-SEC-005)