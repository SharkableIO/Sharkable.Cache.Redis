using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis-backed <see cref="IIdempotencyStore"/> using <see cref="StackExchange.Redis"/>.
/// Keys are prefixed with configurable prefix (default <c>sharkable:idempotency:</c>).
/// In-flight slots store the string <c>"IN_FLIGHT"</c>; completed records store
/// the JSON-serialized <see cref="IdempotencyRecord"/> (via the AOT-safe
/// <see cref="CacheRedisJsonContext"/>).
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private const string InFlightMarker = "IN_FLIGHT";

    private readonly IDatabase _db;
    private readonly string _keyPrefix;
    private readonly ILogger<RedisIdempotencyStore>? _logger;

    /// <summary>Creates a store with default <see cref="RedisStoreOptions"/> and no logger.</summary>
    public RedisIdempotencyStore(IConnectionMultiplexer multiplexer)
        : this(multiplexer, new RedisStoreOptions(), null) { }

    /// <summary>Creates a store with the given options and no logger.</summary>
    public RedisIdempotencyStore(IConnectionMultiplexer multiplexer, RedisStoreOptions options)
        : this(multiplexer, options, null) { }

    /// <summary>
    /// Creates a new <see cref="RedisIdempotencyStore"/> with explicit logger injection.
    /// </summary>
    /// <param name="multiplexer">The Redis connection multiplexer.</param>
    /// <param name="options">Store options.</param>
    /// <param name="logger">Logger for diagnostics; optional.</param>
    public RedisIdempotencyStore(
        IConnectionMultiplexer multiplexer,
        RedisStoreOptions options,
        ILogger<RedisIdempotencyStore>? logger)
    {
        _db = multiplexer.GetDatabase(options.Database);
        _keyPrefix = options.IdempotencyKeyPrefix;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveAsync(string key, TimeSpan inFlightTtl)
    {
        return await _db.StringSetAsync(
            _keyPrefix + key, InFlightMarker, inFlightTtl, When.NotExists);
    }

    /// <inheritdoc />
    /// <remarks>
    /// On JSON-deserialization failure the underlying exception is logged
    /// and re-thrown rather than silently swallowed. The host's exception
    /// handler middleware (<c>UseSharkExceptionHandler</c>) converts this
    /// into a <c>500</c> response, ensuring the downstream handler is
    /// <em>not</em> re-executed on a corrupted idempotency record — this
    /// prevents double-charge scenarios on payment-style endpoints.
    /// </remarks>
    public async Task<IdempotencyLookup?> GetAsync(string key)
    {
        var value = await _db.StringGetAsync(_keyPrefix + key);
        if (value.IsNullOrEmpty)
            return null;

        if (value == InFlightMarker)
            return new IdempotencyInFlight();

        try
        {
            var payload = JsonSerializer.Deserialize(
                (string)value!,
                CacheRedisJsonContext.Default.CacheRedisIdempotencyPayload);
            if (payload is null)
            {
                return null;
            }
            var record = new IdempotencyRecord(
                payload.Key,
                payload.Fingerprint,
                payload.StatusCode,
                payload.ContentType,
                payload.Body,
                payload.CompletedAt);
            return new IdempotencyHit(record);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger?.LogWarning(
                ex,
                "Corrupted idempotency record at key {Key}; failing closed to prevent re-execution.",
                _keyPrefix + key);
            throw new InvalidOperationException(
                $"Corrupted idempotency record at key '{_keyPrefix + key}'. " +
                "Refusing to re-execute downstream handler.",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task StoreAsync(string key, IdempotencyRecord record, TimeSpan ttl)
    {
        var payload = new CacheRedisIdempotencyPayload(
            record.Key,
            record.Fingerprint,
            record.StatusCode,
            record.ContentType,
            record.Body.ToArray(),
            record.CompletedAt);
        var json = JsonSerializer.Serialize(
            payload,
            CacheRedisJsonContext.Default.CacheRedisIdempotencyPayload);
        await _db.StringSetAsync(_keyPrefix + key, json, ttl);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key)
    {
        await _db.KeyDeleteAsync(_keyPrefix + key);
    }
}
