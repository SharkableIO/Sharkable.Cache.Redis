using System.Text.Json.Serialization;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for Cache.Redis payloads.
/// Required for AOT compatibility: eliminates IL2026 / IL3050 from reflective
/// <c>JsonSerializer.Serialize&lt;T&gt;</c> calls in <see cref="RedisIdempotencyStore"/>.
/// Wire-format uses camelCase to match the prior reflective serialization
/// (preserves on-disk format for in-flight keys during rollout).
/// Internal because the only registered type, <see cref="CacheRedisIdempotencyPayload"/>,
/// is also an internal implementation detail of the Redis store.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CacheRedisIdempotencyPayload))]
internal partial class CacheRedisJsonContext : JsonSerializerContext
{
}
