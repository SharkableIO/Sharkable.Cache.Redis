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
    private const string IncrementScript = @"
local count = redis.call('INCR', KEYS[1])
if count == 1 then
    redis.call('EXPIRE', KEYS[1], ARGV[1])
end
return count";

    private readonly IDatabase _db;
    private readonly string _keyPrefix;

    /// <summary>Creates a store with default <see cref="RedisStoreOptions"/>.</summary>
    public RedisRateLimitStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    /// <summary>Creates a store with the given options.</summary>
    public RedisRateLimitStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _keyPrefix = options.RateLimitKeyPrefix;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Rejects <see cref="TimeSpan.Zero"/> and negative windows so the underlying
    /// Lua script never receives <c>EXPIRE key 0</c> (immediate delete) or a
    /// negative TTL (also immediate delete in Redis), which would silently
    /// disable rate limiting.
    /// </remarks>
    public async Task<long> IncrementAsync(string key, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(window),
                "Rate limit window must be positive; zero or negative windows would delete the counter immediately.");
        }

        var redisKey = new RedisKey(_keyPrefix + key);
        var ttlSeconds = (long)Math.Ceiling(window.TotalSeconds);
        var result = await _db.ScriptEvaluateAsync(
            IncrementScript, new[] { redisKey }, new RedisValue[] { ttlSeconds });
        return (long)result;
    }

    /// <inheritdoc />
    public async Task ResetAsync(string key)
    {
        await _db.KeyDeleteAsync(_keyPrefix + key);
    }
}
