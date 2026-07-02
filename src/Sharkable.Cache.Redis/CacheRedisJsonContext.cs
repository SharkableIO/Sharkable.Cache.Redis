using System.Text.Json.Serialization;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for Cache.Redis payloads.
/// Required for AOT compatibility: eliminates IL2026 / IL3050 from reflective
/// <c>JsonSerializer.Serialize&lt;T&gt;</c> calls in <see cref="RedisIdempotencyStore"/>
/// and <see cref="RedisCronJobStore"/>. Wire-format uses camelCase to match
/// the prior reflective serialization (preserves on-disk format for in-flight
/// keys during rollout).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CacheRedisIdempotencyPayload))]
[JsonSerializable(typeof(CronJobState))]
internal partial class CacheRedisJsonContext : JsonSerializerContext
{
}
