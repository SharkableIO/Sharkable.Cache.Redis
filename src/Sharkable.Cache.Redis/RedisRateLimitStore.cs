using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="IDistributedRateLimitStore"/> using
/// <see cref="StackExchange.Redis"/>. Uses an atomic Lua script
/// (<c>INCR</c> + conditional <c>EXPIRE</c>) for correct distributed
/// counting within a fixed window.
/// </summary>
public sealed class RedisRateLimitStore : IDistributedRateLimitStore
{
    private const string KeyPrefix = "sharkable:ratelimit:";

    private const string IncrementScript = @"
local count = redis.call('INCR', KEYS[1])
if count == 1 then
    redis.call('EXPIRE', KEYS[1], ARGV[1])
end
return count";

    private readonly IDatabase _db;

    /// <summary>
    /// Creates a new <see cref="RedisRateLimitStore"/> backed by the given
    /// <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    public RedisRateLimitStore(IConnectionMultiplexer multiplexer)
    {
        _db = multiplexer.GetDatabase();
    }

    /// <inheritdoc />
    public async Task<long> IncrementAsync(string key, TimeSpan window)
    {
        var redisKey = new RedisKey(KeyPrefix + key);
        var ttlSeconds = (long)Math.Ceiling(window.TotalSeconds);
        var result = await _db.ScriptEvaluateAsync(
            IncrementScript, new[] { redisKey }, new RedisValue[] { ttlSeconds });
        return (long)result;
    }

    /// <inheritdoc />
    public async Task ResetAsync(string key)
    {
        await _db.KeyDeleteAsync(KeyPrefix + key);
    }
}
