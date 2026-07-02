namespace Sharkable.Cache.Redis;

/// <summary>
/// Options for Redis-backed store implementations.
/// Configured via <c>AddSharkableRedis(connectionString, opt => ...)</c>.
/// </summary>
public sealed class RedisStoreOptions
{
    /// <summary>Key prefix for idempotency store. Default: <c>"sharkable:idempotency:"</c>.</summary>
    public string IdempotencyKeyPrefix { get; set; } = "sharkable:idempotency:";

    /// <summary>Key prefix for rate limit store. Default: <c>"sharkable:ratelimit:"</c>.</summary>
    public string RateLimitKeyPrefix { get; set; } = "sharkable:ratelimit:";

    /// <summary>Key prefix for saga distributed locks. Default: <c>"sharkable:saga:lock:"</c>.</summary>
    public string SagaLockPrefix { get; set; } = "sharkable:saga:lock:";

    /// <summary>Key prefix for saga progress. Default: <c>"sharkable:saga:progress:"</c>.</summary>
    public string SagaProgressPrefix { get; set; } = "sharkable:saga:progress:";

    /// <summary>Key prefix for cron job distributed locks. Default: <c>"sharkable:cron:lock:"</c>.</summary>
    public string CronLockPrefix { get; set; } = "sharkable:cron:lock:";

    /// <summary>Redis hash key for cron job states. Default: <c>"sharkable:cron:states"</c>.</summary>
    public string CronStateKey { get; set; } = "sharkable:cron:states";

    /// <summary>Redis database number. Default: <c>-1</c> (default database).</summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// When <c>true</c>, the Redis connection MUST use TLS. If the supplied
    /// connection string does not already specify <c>ssl=true</c>, the
    /// configuration is upgraded to require TLS before connecting.
    /// Default: <c>false</c>.
    /// </summary>
    public bool RequireTls { get; set; }

    /// <summary>
    /// TTL applied to saga progress records (<c>sharkable:saga:progress:&lt;id&gt;</c>).
    /// After this window an in-flight saga's progress is lost; configure
    /// higher than your slowest saga + retry budget. Default: 7 days.
    /// </summary>
    public TimeSpan SagaProgressTtl { get; set; } = TimeSpan.FromDays(7);
}
