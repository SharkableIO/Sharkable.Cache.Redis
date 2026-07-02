using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Extension methods for registering Sharkable Redis-backed stores
/// in the DI container. Call before <c>builder.Services.AddShark()</c>.
/// </summary>
public static class SharkableRedisExtensions
{
    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> (singleton) and
    /// swaps the default stores for their Redis-backed implementations.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="connectionString">Redis connection string (e.g. <c>"localhost:6379"</c>). Required.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisStoreOptions"/>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="connectionString"/> is null or whitespace.
    /// </exception>
    /// <remarks>
    /// Defaults applied only when not already present in the connection string:
    /// <c>abortConnect=false</c> so the multiplexer keeps reconnecting instead
    /// of crashing the host when Redis is briefly unavailable at startup.
    /// An explicit <c>abortConnect=true</c> in <paramref name="connectionString"/>
    /// is respected and never silently downgraded.
    /// Set <see cref="RedisStoreOptions.RequireTls"/> in <paramref name="configure"/>
    /// to enforce TLS (<c>ssl=true</c>) on the resulting connection.
    /// <para>
    /// To surface the Redis health check on the public <c>/healthz</c> endpoint,
    /// call <c>UseSharkableRedisHealthCheck()</c> explicitly after this method.
    /// It is not auto-wired — consumers opt in.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services,
        string connectionString,
        Action<RedisStoreOptions>? configure = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "Redis connection string is required.", nameof(connectionString));
        }

        var options = new RedisStoreOptions();
        configure?.Invoke(options);

        var config = ConfigurationOptions.Parse(connectionString);

        var hadSsl = config.Ssl;
        if (!ConnectionStringHasKey(connectionString, "abortConnect"))
        {
            config.AbortOnConnectFail = false;
        }
        if (options.RequireTls && !hadSsl)
        {
            config.Ssl = true;
        }

        var loggerFactory = services.BuildServiceProvider().GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("Sharkable.Cache.Redis.AddSharkableRedis");
        logger?.LogInformation(
            "Connecting to Redis (abortConnect={Abort}, ssl={Ssl}, requireTls={RequireTls}).",
            config.AbortOnConnectFail, config.Ssl, options.RequireTls);

        var multiplexer = ConnectionMultiplexer.Connect(config);
        return services.AddSharkableRedis(multiplexer, configure);
    }

    /// <summary>
    /// Uses an existing <see cref="IConnectionMultiplexer"/> and swaps
    /// the default stores for their Redis-backed implementations.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="multiplexer">A pre-configured <see cref="IConnectionMultiplexer"/>.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisStoreOptions"/>.</param>
    /// <remarks>
    /// To surface the Redis health check on the public <c>/healthz</c> endpoint,
    /// call <c>UseSharkableRedisHealthCheck()</c> explicitly after this method.
    /// It is not auto-wired — consumers opt in.
    /// <para>
    /// The <see cref="IConnectionMultiplexer"/> is registered as a singleton
    /// against the interface and owned by <see cref="RedisMultiplexerDisposalService"/>,
    /// which calls <see cref="IAsyncDisposable.DisposeAsync"/> on host stop so
    /// in-flight commands drain gracefully. Without this, DI's default sync
    /// disposal would force-close pending socket writes during shutdown.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisStoreOptions>? configure = null)
    {
        var options = new RedisStoreOptions();
        configure?.Invoke(options);
        options.ValidatePrefixes();

        services.AddSingleton<IConnectionMultiplexer>(_ => multiplexer);
        services.AddSingleton<RedisMultiplexerDisposalService>();
        services.AddHostedService(sp => sp.GetRequiredService<RedisMultiplexerDisposalService>());
        services.AddSingleton(options);
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddSingleton<IDistributedRateLimitStore, RedisRateLimitStore>();
        services.AddSingleton<ISagaStore, RedisSagaStore>();
        services.AddSingleton<ICronJobStore, RedisCronJobStore>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="IConnectionMultiplexer"/> (singleton) from
    /// <see cref="ConfigurationOptions"/> and swaps the default stores
    /// for their Redis-backed implementations.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="configuration">Redis configuration options.</param>
    /// <param name="configure">Optional callback to configure <see cref="RedisStoreOptions"/>.</param>
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services,
        ConfigurationOptions configuration,
        Action<RedisStoreOptions>? configure = null)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var multiplexer = ConnectionMultiplexer.Connect(configuration);
        return services.AddSharkableRedis(multiplexer, configure);
    }

    /// <summary>
    /// Returns <c>true</c> if the connection string contains the given
    /// configuration key. Used to detect whether the user explicitly set
    /// a value (so we don't silently override their choice). Comparison is
    /// case-insensitive and matches keys as full segments separated by
    /// <c>,</c> with optional surrounding whitespace.
    /// </summary>
    /// <param name="connectionString">The raw Redis connection string.</param>
    /// <param name="key">The configuration key to look for (e.g. <c>"abortConnect"</c>).</param>
    private static bool ConnectionStringHasKey(string connectionString, string key)
    {
        foreach (var part in connectionString.Split(','))
        {
            var trimmed = part.AsSpan().Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            if (trimmed[..eq].Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
