using System.Text.Json;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ICronJobStore"/> with distributed locking
/// via SETNX and state persistence via Hash.
/// </summary>
public sealed class RedisCronJobStore : ICronJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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

    public Task<bool> TryAcquireJobLockAsync(string jobName, TimeSpan ttl)
        => _db.StringSetAsync(_lockPrefix + jobName, Environment.MachineName, ttl, When.NotExists);

    public Task ReleaseJobLockAsync(string jobName)
    {
        _db.KeyDelete(_lockPrefix + jobName);
        return Task.CompletedTask;
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
}
