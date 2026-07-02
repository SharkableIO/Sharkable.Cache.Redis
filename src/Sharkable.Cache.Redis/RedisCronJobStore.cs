using System.Collections.Concurrent;
using System.Text.Json;
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly IDatabase _db;
    private readonly string _lockPrefix;
    private readonly string _stateKey;

    private readonly ConcurrentDictionary<string, string> _lockTokens = new();

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
    /// fencing token. The token is stored locally so the matching
    /// <see cref="ReleaseJobLockAsync"/> only deletes the lock if the stored
    /// value still matches, preventing an instance whose lock TTL expired
    /// mid-execution from deleting a lock now held by another instance.
    /// </summary>
    /// <param name="jobName">Unique cron job identifier.</param>
    /// <param name="ttl">Lock time-to-live.</param>
    /// <returns><c>true</c> if the lock was acquired; <c>false</c> otherwise.</returns>
    public async Task<bool> TryAcquireJobLockAsync(string jobName, TimeSpan ttl)
    {
        var token = Guid.NewGuid().ToString("N");
        var acquired = await _db.StringSetAsync(
            _lockPrefix + jobName, token, ttl, When.NotExists);
        if (acquired)
        {
            _lockTokens[jobName] = token;
        }
        return acquired;
    }

    /// <summary>
    /// Releases the cron job's distributed lock using a check-and-delete Lua
    /// script: only deletes the lock key if the stored value matches the token
    /// captured at acquire time.
    /// </summary>
    /// <param name="jobName">Unique cron job identifier.</param>
    public async Task ReleaseJobLockAsync(string jobName)
    {
        if (!_lockTokens.TryRemove(jobName, out var token))
        {
            return;
        }

        await _db.ScriptEvaluateAsync(
            LockReleaseScript,
            new { key = (RedisKey)(_lockPrefix + jobName), token = (RedisValue)token });
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
