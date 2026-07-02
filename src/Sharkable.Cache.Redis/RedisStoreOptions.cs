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

    /// <summary>
    /// Redis hash key for cron job states. Default: <c>"sharkable:cron:states"</c>.
    /// <para>
    /// <b>Multi-tenant deployments:</b> the default value is a single shared
    /// key for all tenants on the same Redis instance. To shard per-tenant,
    /// set this to a tenant-scoped value (e.g.
    /// <c>"sharkable:cron:states:tenant-{tenantId}"</c>) inside the
    /// <c>configure</c> callback of <c>AddSharkableRedis</c>. The same
    /// constraint applies to all other prefix properties on this class.
    /// </para>
    /// </summary>
    public string CronStateKey { get; set; } = "sharkable:cron:states";

    private int _database = -1;

    /// <summary>
    /// Redis database number. <c>-1</c> means "use the multiplexer's default DB".
    /// Values outside <c>[-1, 15]</c> are silently clamped to the nearest valid value
    /// (Redis ships with 16 logical databases, <c>0..15</c>); passing
    /// <c>1000</c> previously crashed on the first command via
    /// <c>multiplexer.GetDatabase(1000)</c>.
    /// </summary>
    public int Database
    {
        get => _database;
        set => _database = value < -1 ? -1 : (value > 15 ? 15 : value);
    }

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

    /// <summary>
    /// Validates all key-prefix properties in this instance. Called by
    /// <c>AddSharkableRedis</c> after the configuration callback runs so that
    /// malformed prefixes fail at startup with a clear error rather than
    /// silently producing collision-prone keys at runtime.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when any prefix is empty, contains characters outside
    /// <c>[A-Za-z0-9:_-]</c>, or — for true namespace prefixes — does not
    /// terminate with <c>:</c> (which can cause prefix collisions like
    /// <c>"sharkable:saga"</c> vs <c>"sharkable:saga:lock"</c>).
    /// </exception>
    internal void ValidatePrefixes()
    {
        ValidateKeyPrefix(nameof(IdempotencyKeyPrefix), IdempotencyKeyPrefix);
        ValidateKeyPrefix(nameof(RateLimitKeyPrefix), RateLimitKeyPrefix);
        ValidateKeyPrefix(nameof(SagaLockPrefix), SagaLockPrefix);
        ValidateKeyPrefix(nameof(SagaProgressPrefix), SagaProgressPrefix);
        ValidateKeyPrefix(nameof(CronLockPrefix), CronLockPrefix);
        ValidateKeyPrefix(nameof(CronStateKey), CronStateKey, requireTrailingColon: false);
    }

    /// <summary>
    /// Validates a single key prefix.
    /// </summary>
    /// <param name="propertyName">Caller <c>nameof</c> for the error message.</param>
    /// <param name="prefix">The prefix value to validate.</param>
    /// <param name="requireTrailingColon">
    /// When <c>true</c> (default for namespace prefixes), the value must end
    /// with <c>:</c> so it cannot collide with a longer prefix that happens
    /// to share a leading substring.
    /// </param>
    private static void ValidateKeyPrefix(string propertyName, string prefix, bool requireTrailingColon = true)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            throw new ArgumentException(
                $"{propertyName} must be non-empty; an empty prefix collides across tenants and applications sharing the same Redis instance.",
                propertyName);
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            var c = prefix[i];
            var isAllowed =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == ':' || c == '_' || c == '-';
            if (!isAllowed)
            {
                throw new ArgumentException(
                    $"{propertyName} contains invalid character '{c}' at index {i}; only [A-Za-z0-9:_-] are allowed.",
                    propertyName);
            }
        }

        if (requireTrailingColon && prefix[^1] != ':')
        {
            throw new ArgumentException(
                $"{propertyName} must terminate with ':' to prevent prefix collisions like 'sharkable:saga' vs 'sharkable:saga:lock'.",
                propertyName);
        }
    }
}
