using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ISagaStore"/> using <see cref="StackExchange.Redis"/>.
/// Distributed lock via <c>SETNX</c> with per-acquire fencing tokens and
/// check-and-delete release (Lua); progress via <c>SET/GET/DEL</c>.
/// Registered automatically by <c>AddSharkableRedis()</c>.
/// </summary>
public sealed class RedisSagaStore : ISagaStore
{
    private static readonly LuaScript LockReleaseScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then " +
        "return redis.call('DEL', @key) " +
        "else return 0 end");

    private static readonly LuaScript LockRenewScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @token then " +
        "return redis.call('PEXPIRE', @key, @ttlMs) " +
        "else return 0 end");

    /// <summary>
    /// In-process cache of fencing tokens keyed by <c>sagaId</c>. Bounded
    /// (<see cref="MemoryCacheOptions.SizeLimit"/>) with sliding expiration
    /// equal to <c>LockTtl * 3</c> so a token whose holder crashes without
    /// releasing is reclaimed automatically, preventing unbounded growth
    /// for crash-recovery sagaIds. The Redis-side TTL remains the source of
    /// truth for lock validity; this cache only exists so that the local
    /// process can prove ownership before calling <see cref="LockReleaseScript"/>
    /// or <see cref="LockRenewScript"/>.
    /// </summary>
    private static readonly MemoryCache _lockTokens = new(new MemoryCacheOptions
    {
        SizeLimit = 10_000,
    });

    private readonly IDatabase _db;
    private readonly string _lockPrefix;
    private readonly string _progressPrefix;
    private readonly TimeSpan _progressTtl;

    /// <summary>Creates a store with default <see cref="RedisStoreOptions"/>.</summary>
    public RedisSagaStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    /// <summary>Creates a store with the given options.</summary>
    public RedisSagaStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _lockPrefix = options.SagaLockPrefix;
        _progressPrefix = options.SagaProgressPrefix;
        _progressTtl = options.SagaProgressTtl;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Generates a fresh per-acquire <see cref="Guid"/> as the fencing token
    /// and stores it in the local token cache with sliding expiration
    /// <c>ttl * 3</c>. Release and renewal only act on the lock if the
    /// stored token still matches, preventing split-brain when the lock TTL
    /// expires mid-work and another instance acquires the same key. If the
    /// local token entry expires (because the holder never renewed or
    /// released), subsequent release/renew calls become no-ops; the caller
    /// should treat this as "lock no longer owned by this process".
    /// <para>
    /// Rejects non-positive <paramref name="ttl"/> because <c>SET key value EX 0 NX</c>
    /// always fails in Redis (a zero TTL deletes the key immediately), so the
    /// lock would never be acquired and the saga would block forever.
    /// </para>
    /// </remarks>
    public async Task<bool> TryAcquireLockAsync(string sagaId, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                "Saga lock TTL must be positive; a zero TTL would make acquisition always fail (EX 0 deletes the key).");
        }

        var token = Guid.NewGuid().ToString("N");
        var acquired = await _db.StringSetAsync(
            _lockPrefix + sagaId, token, ttl, When.NotExists);
        if (acquired)
        {
            SetToken(_lockPrefix + sagaId, token, ttl);
        }
        return acquired;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomically extends the lock TTL only if the stored value still matches
    /// the token captured at acquire time. If the lock has been taken over by
    /// another instance, the local token record is cleared so subsequent
    /// release/renew calls become no-ops. <see cref="MemoryCache.TryGetValue"/> also
    /// resets the sliding expiration on the token entry so a frequently
    /// renewed saga never loses its token. Non-positive <paramref name="ttl"/>
    /// is silently treated as a no-op to match the documented contract.
    /// </remarks>
    public async Task RenewLockAsync(string sagaId, TimeSpan ttl)
    {
        var token = GetToken(_lockPrefix + sagaId);
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
            new { key = (RedisKey)(_lockPrefix + sagaId), token = (RedisValue)token, ttlMs = (RedisValue)ttlMs });

        if ((long)result == 0)
        {
            _lockTokens.Remove(_lockPrefix + sagaId);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs a check-and-delete Lua script: only deletes the lock key if the
    /// stored value matches the token captured at acquire time. This prevents
    /// an instance whose lock TTL expired mid-work from deleting a lock now
    /// held by another instance.
    /// </remarks>
    public async Task ReleaseLockAsync(string sagaId)
    {
        if (_lockTokens.TryGetValue(_lockPrefix + sagaId, out var cached) && cached is string token)
        {
            _lockTokens.Remove(_lockPrefix + sagaId);

            await _db.ScriptEvaluateAsync(
                LockReleaseScript,
                new { key = (RedisKey)(_lockPrefix + sagaId), token = (RedisValue)token });
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The progress record is written with a TTL of <see cref="RedisStoreOptions.SagaProgressTtl"/>
    /// (default 7 days) so a host crash that orphans the saga cannot leak
    /// Redis keys indefinitely. Configure this higher than your slowest
    /// expected saga duration plus retry budget.
    /// </remarks>
    public async Task SaveProgressAsync(string sagaId, int stepIndex, CancellationToken ct)
    {
        await _db.StringSetAsync(_progressPrefix + sagaId, stepIndex, _progressTtl);
    }

    /// <inheritdoc />
    public async Task<int> LoadProgressAsync(string sagaId, CancellationToken ct)
    {
        var val = await _db.StringGetAsync(_progressPrefix + sagaId);
        return val.HasValue ? (int)val : -1;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Progress key is deleted unconditionally (progress is per-saga, not
    /// per-lock-holder). Lock key is deleted only if the stored value matches
    /// the locally captured token, using the same check-and-delete Lua script
    /// as <see cref="ReleaseLockAsync"/>.
    /// </remarks>
    public async Task DeleteAsync(string sagaId, CancellationToken ct)
    {
        string? token = null;
        if (_lockTokens.TryGetValue(_lockPrefix + sagaId, out var cached) && cached is string t)
        {
            token = t;
            _lockTokens.Remove(_lockPrefix + sagaId);
        }

        Task lockTask;
        if (token is not null)
        {
            lockTask = _db.ScriptEvaluateAsync(
                LockReleaseScript,
                new { key = (RedisKey)(_lockPrefix + sagaId), token = (RedisValue)token });
        }
        else
        {
            lockTask = Task.CompletedTask;
        }

        await Task.WhenAll(
            _db.KeyDeleteAsync(_progressPrefix + sagaId),
            lockTask);
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
    /// expiration, so a frequently renewed or released saga never loses
    /// its token to the size-bound eviction policy.
    /// </summary>
    private static string? GetToken(string key) =>
        _lockTokens.TryGetValue(key, out var v) ? v as string : null;
}