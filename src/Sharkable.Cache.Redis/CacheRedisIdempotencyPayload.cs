namespace Sharkable.Cache.Redis;

/// <summary>
/// Local wire-format projection of <see cref="IdempotencyRecord"/> used for
/// JSON storage in Redis. A wrapper type is required because the core
/// <see cref="IdempotencyRecord"/> is declared as <c>public sealed record</c>
/// in a separate assembly and is not <c>partial</c>; the source-generated
/// <see cref="CacheRedisJsonContext"/> can serialize this type directly
/// without runtime reflection, keeping Cache.Redis AOT-compatible.
/// <see cref="Body"/> is stored as <c>byte[]</c> here (the JSON-friendly form)
/// and projected to <see cref="ReadOnlyMemory{T}"/> on the way in/out.
/// </summary>
internal sealed record CacheRedisIdempotencyPayload(
    string Key,
    string Fingerprint,
    int StatusCode,
    string ContentType,
    byte[] Body,
    DateTimeOffset CompletedAt);
