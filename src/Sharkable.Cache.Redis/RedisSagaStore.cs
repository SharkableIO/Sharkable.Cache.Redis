using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ISagaStore"/> using <see cref="StackExchange.Redis"/>.
/// Distributed lock via <c>SETNX</c>, progress via <c>SET/GET/DEL</c>.
/// Registered automatically by <c>AddSharkableRedis()</c>.
/// </summary>
public sealed class RedisSagaStore : ISagaStore
{
    private const string LockPrefix = "sharkable:saga:lock:";
    private const string ProgressPrefix = "sharkable:saga:progress:";

    private readonly IDatabase _db;

    public RedisSagaStore(IConnectionMultiplexer multiplexer)
    {
        _db = multiplexer.GetDatabase();
    }

    /// <inheritdoc />
    public async Task<bool> TryAcquireLockAsync(string sagaId, TimeSpan ttl)
    {
        return await _db.StringSetAsync(
            LockPrefix + sagaId, Environment.MachineName, ttl, When.NotExists);
    }

    /// <inheritdoc />
    public Task ReleaseLockAsync(string sagaId)
    {
        _db.KeyDelete(LockPrefix + sagaId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SaveProgressAsync(string sagaId, int stepIndex, CancellationToken ct)
    {
        await _db.StringSetAsync(ProgressPrefix + sagaId, stepIndex);
    }

    /// <inheritdoc />
    public async Task<int> LoadProgressAsync(string sagaId, CancellationToken ct)
    {
        var val = await _db.StringGetAsync(ProgressPrefix + sagaId);
        return val.HasValue ? (int)val : -1;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sagaId, CancellationToken ct)
    {
        _db.KeyDelete(ProgressPrefix + sagaId);
        _db.KeyDelete(LockPrefix + sagaId);
        return Task.CompletedTask;
    }
}
