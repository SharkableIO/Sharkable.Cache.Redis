using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ICronJobStore"/> with distributed locking
/// via <c>SETNX</c> with per-acquire fencing tokens and check-and-delete
/// release (Lua); state persistence via Hash.
/// </summary>
public sealed class RedisCronJobStore : ICronJobStore
{
    private static readonly LuaScript LockReleaseScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then " +
        "return redis.call('DEL', @key) " +
        "else return 0 end");

    private static readonly LuaScript LockRenewScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then " +
        "return redis.call('PEXPIRE', @key, @ttlMs) " +
        "else return 0 end");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// In-process cache of fencing tokens keyed by <c>jobName</c>. Bounded
    /// (<see cref="MemoryCacheOptions.SizeLimit"/>) with sliding expiration
    /// equal to <c>LockTtl * 3</c> so a token whose holder crashes without
    /// releasing is reclaimed automatically, preventing unbounded growth
    /// for cron jobs whose handlers never complete (timeout, hang, OOM).
    /// The Redis-side TTL remains the source of truth for lock validity;
    /// this cache only exists so that the local process can prove
    /// ownership before calling <see cref="LockReleaseScript"/> or
    /// <see cref="LockRenewScript"/>.
    /// </summary>
    private static readonly MemoryCache _lockTokens = new(new MemoryCacheOptions
    {
        SizeLimit = 10_000,
    });

    private readonly IDatabase _db;
    private readonly string _lockPrefix;
    private readonly string _stateKey;

    public RedisCronJobStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    public RedisCronJobStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _lockPrefix = options.CronLockPrefix;
        _stateKey = options.CronStateKey;
    }

    /// <summary>
    /// Acquires the cron job's distributed lock, generating a fresh per-acquire
    /// fencing token. The token is stored in the local cache with sliding
    /// expiration <c>ttl * 3</c> so the matching <see cref="ReleaseJobLockAsync"/>
    /// only deletes the lock if the stored value still matches, preventing
    /// an instance whose lock TTL expired mid-execution from deleting a lock
    /// now held by another instance.
    /// </summary>
    /// <param name="jobName">Unique cron job identifier.</param>
    /// <param name="ttl">Lock time-to-live. Must be positive.</param>
    /// <returns><c>true</c> if the lock was acquired; <c>false</c> otherwise.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="ttl"/> is zero or negative.
    /// </exception>
    public async Task<bool> TryAcquireJobLockAsync(string jobName, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "Cron lock TTL must be positive; a zero TTL would make acquisition always fail (EX 0 deletes the key).");
        }

        var token = Guid.NewGuid().ToString("N");
        var acquired = await _db.StringSetAsync(
            _lockPrefix + jobName, token, ttl, When.NotExists);
        if (acquired)
        {
            SetToken(_lockPrefix + jobName, token, ttl);
        }
        return acquired;
    }

    /// <summary>
    /// Extends the TTL of an already-held cron job lock. Atomically renews
    /// the lock only if the stored value still matches the token captured at
    /// acquire time, preventing an instance whose lock has been taken over
    /// from extending someone else's TTL. On mismatch, the local token entry
    /// is cleared so subsequent release/renew calls become no-ops.
    /// </summary>
    /// <param name="jobName">Unique cron job identifier.</param>
    /// <param name="ttl">New lock TTL to apply (typically equal to the
    /// original LockTtl so the renewal cadence is consistent).</param>
    public async Task RenewJobLockAsync(string jobName, TimeSpan ttl)
    {
        var token = GetToken(_lockPrefix + jobName);
        if (token is null)
        {
            return;
        }

        var ttlMs = (long)ttl.TotalMilliseconds;
        if (ttlMs <= 0)
        {
            return;
        }

        var result = (RedisResult)await _db.ScriptEvaluateAsync(
            LockRenewScript,
            new { key = (RedisKey)(_lockPrefix + jobName), token = (RedisValue)token, ttlMs = (RedisValue)ttlMs });

        if ((long)result == 0)
        {
            _lockTokens.Remove(_lockPrefix + jobName);
        }
    }

    /// <summary>
    /// Releases the cron job's distributed lock using a check-and-delete Lua
    /// script: only deletes the lock key if the stored value matches the token
    /// captured at acquire time.
    /// </summary>
    /// <param name="jobName">Unique cron job identifier.</param>
    public async Task ReleaseJobLockAsync(string jobName)
    {
        if (_lockTokens.TryGetValue(_lockPrefix + jobName, out var cached) && cached is string token)
        {
            _lockTokens.Remove(_lockPrefix + jobName);

            await _db.ScriptEvaluateAsync(
                LockReleaseScript,
                new { key = (RedisKey)(_lockPrefix + jobName), token = (RedisValue)token });
        }
    }

    public async Task SaveStateAsync(string jobName, CronJobState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await _db.HashSetAsync(_stateKey, jobName, json);
    }

    public async Task<CronJobState?> LoadStateAsync(string jobName)
    {
        var json = await _db.HashGetAsync(_stateKey, jobName);
        return json.HasValue
            ? JsonSerializer.Deserialize<CronJobState>(json.ToString(), JsonOptions)
            : null;
    }

    public async Task<IReadOnlyList<CronJobState>> ListStatesAsync()
    {
        var entries = await _db.HashGetAllAsync(_stateKey);
        return entries
            .Where(e => e.Value.HasValue)
            .Select(e => JsonSerializer.Deserialize<CronJobState>(e.Value.ToString(), JsonOptions)!)
            .ToList();
    }

    /// <summary>
    /// Stores <paramref name="token"/> under <paramref name="key"/> with
    /// sliding expiration <c>ttl * 3</c> so that callers that renew or
    /// release the lock keep the entry alive, while a crashed holder's
    /// token is reclaimed automatically.
    /// </summary>
    private static void SetToken(string key, string token, TimeSpan ttl)
    {
        using var entry = _lockTokens.CreateEntry(key);
        entry.SlidingExpiration = TimeSpan.FromTicks(Math.Max(ttl.Ticks, 1) * 3);
        entry.Size = 1;
        entry.Value = token;
    }

    /// <summary>
    /// Looks up the locally-captured fencing token for <paramref name="key"/>.
    /// <see cref="MemoryCache.TryGetValue"/> also resets the sliding
    /// expiration, so a frequently renewed or released job never loses
    /// its token to the size-bound eviction policy.
    /// </summary>
    private static string? GetToken(string key) =>
        _lockTokens.TryGetValue(key, out var v) ? v as string : null;
}