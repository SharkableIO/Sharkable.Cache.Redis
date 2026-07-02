using System.Collections.Concurrent;
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

    private readonly IDatabase _db;
    private readonly string _lockPrefix;
    private readonly string _progressPrefix;

    private readonly ConcurrentDictionary<string, string> _lockTokens = new();

    public RedisSagaStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    public RedisSagaStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _lockPrefix = options.SagaLockPrefix;
        _progressPrefix = options.SagaProgressPrefix;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Generates a fresh per-acquire <see cref="Guid"/> as the fencing token
    /// and stores it locally. Release and renewal only act on the lock if the
    /// stored token still matches, preventing split-brain when the lock TTL
    /// expires mid-work and another instance acquires the same key.
    /// </remarks>
    public async Task<bool> TryAcquireLockAsync(string sagaId, TimeSpan ttl)
    {
        var token = Guid.NewGuid().ToString("N");
        var acquired = await _db.StringSetAsync(
            _lockPrefix + sagaId, token, ttl, When.NotExists);
        if (acquired)
        {
            _lockTokens[sagaId] = token;
        }
        return acquired;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomically extends the lock TTL only if the stored value still matches
    /// the token captured at acquire time. If the lock has been taken over by
    /// another instance, the local token record is cleared so subsequent
    /// release/renew calls become no-ops.
    /// </remarks>
    public async Task RenewLockAsync(string sagaId, TimeSpan ttl)
    {
        if (!_lockTokens.TryGetValue(sagaId, out var token))
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
            _lockTokens.TryRemove(sagaId, out _);
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
        if (!_lockTokens.TryRemove(sagaId, out var token))
        {
            return;
        }

        await _db.ScriptEvaluateAsync(
            LockReleaseScript,
            new { key = (RedisKey)(_lockPrefix + sagaId), token = (RedisValue)token });
    }

    /// <inheritdoc />
    public async Task SaveProgressAsync(string sagaId, int stepIndex, CancellationToken ct)
    {
        await _db.StringSetAsync(_progressPrefix + sagaId, stepIndex);
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
        var token = _lockTokens.TryRemove(sagaId, out var t) ? t : null;

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
}
