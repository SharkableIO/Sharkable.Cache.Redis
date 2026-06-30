using System.Text.Json;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="IIdempotencyStore"/> using <see cref="StackExchange.Redis"/>.
/// Keys are prefixed with configurable prefix (default <c>sharkable:idempotency:</c>).
/// In-flight slots store the string <c>"IN_FLIGHT"</c>; completed records store
/// the JSON-serialized <see cref="IdempotencyRecord"/>.
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private const string InFlightMarker = "IN_FLIGHT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IDatabase _db;
    private readonly string _keyPrefix;

    public RedisIdempotencyStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions()) { }

    public RedisIdempotencyStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _keyPrefix = options.IdempotencyKeyPrefix;
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveAsync(string key, TimeSpan inFlightTtl)
    {
        return await _db.StringSetAsync(
            _keyPrefix + key, InFlightMarker, inFlightTtl, When.NotExists);
    }

    /// <inheritdoc />
    public async Task<IdempotencyLookup?> GetAsync(string key)
    {
        var value = await _db.StringGetAsync(_keyPrefix + key);
        if (value.IsNullOrEmpty)
            return null;

        if (value == InFlightMarker)
            return new IdempotencyInFlight();

        try
        {
            var record = JsonSerializer.Deserialize<IdempotencyRecord>((string)value!, JsonOptions);
            return record is not null ? new IdempotencyHit(record) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task StoreAsync(string key, IdempotencyRecord record, TimeSpan ttl)
    {
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await _db.StringSetAsync(_keyPrefix + key, json, ttl);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key)
    {
        await _db.KeyDeleteAsync(_keyPrefix + key);
    }
}
