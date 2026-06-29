using System.Text.Json;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="ICronJobStore"/> with distributed locking
/// via SETNX and state persistence via Hash.
/// </summary>
public sealed class RedisCronJobStore : ICronJobStore
{
    private const string LockPrefix = "sharkable:cron:lock:";
    private const string StateKey = "sharkable:cron:states";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDatabase _db;

    public RedisCronJobStore(IConnectionMultiplexer multiplexer)
    {
        _db = multiplexer.GetDatabase();
    }

    public Task<bool> TryAcquireJobLockAsync(string jobName, TimeSpan ttl)
        => _db.StringSetAsync(LockPrefix + jobName, Environment.MachineName, ttl, When.NotExists);

    public Task ReleaseJobLockAsync(string jobName)
    {
        _db.KeyDelete(LockPrefix + jobName);
        return Task.CompletedTask;
    }

    public async Task SaveStateAsync(string jobName, CronJobState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await _db.HashSetAsync(StateKey, jobName, json);
    }

    public async Task<CronJobState?> LoadStateAsync(string jobName)
    {
        var json = await _db.HashGetAsync(StateKey, jobName);
        return json.HasValue
            ? JsonSerializer.Deserialize<CronJobState>(json.ToString(), JsonOptions)
            : null;
    }

    public async Task<IReadOnlyList<CronJobState>> ListStatesAsync()
    {
        var entries = await _db.HashGetAllAsync(StateKey);
        return entries
            .Where(e => e.Value.HasValue)
            .Select(e => JsonSerializer.Deserialize<CronJobState>(e.Value.ToString(), JsonOptions)!)
            .ToList();
    }
}
