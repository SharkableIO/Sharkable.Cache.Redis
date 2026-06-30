using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ISagaStore"/> using <see cref="StackExchange.Redis"/>.
/// Distributed lock via <c>SETNX</c>, progress via <c>SET/GET/DEL</c>.
/// Registered automatically by <c>AddSharkableRedis()</c>.
/// </summary>
public sealed class RedisSagaStore : ISagaStore
{
    private readonly IDatabase _db;
    private readonly string _lockPrefix;
    private readonly string _progressPrefix;

    public RedisSagaStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    public RedisSagaStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _lockPrefix = options.SagaLockPrefix;
        _progressPrefix = options.SagaProgressPrefix;
    }

    public async Task<bool> TryAcquireLockAsync(string sagaId, TimeSpan ttl)
    {
        return await _db.StringSetAsync(
            _lockPrefix + sagaId, Environment.MachineName, ttl, When.NotExists);
    }

    public Task ReleaseLockAsync(string sagaId)
    {
        _db.KeyDelete(_lockPrefix + sagaId);
        return Task.CompletedTask;
    }

    public async Task SaveProgressAsync(string sagaId, int stepIndex, CancellationToken ct)
    {
        await _db.StringSetAsync(_progressPrefix + sagaId, stepIndex);
    }

    public async Task<int> LoadProgressAsync(string sagaId, CancellationToken ct)
    {
        var val = await _db.StringGetAsync(_progressPrefix + sagaId);
        return val.HasValue ? (int)val : -1;
    }

    public Task DeleteAsync(string sagaId, CancellationToken ct)
    {
        _db.KeyDelete(_progressPrefix + sagaId);
        _db.KeyDelete(_lockPrefix + sagaId);
        return Task.CompletedTask;
    }
}
