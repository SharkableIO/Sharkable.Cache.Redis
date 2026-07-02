using Microsoft.Extensions.DependencyInjection;
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
    /// Defaults applied if not present in the connection string:
    /// <c>abortConnect=false</c> so the multiplexer keeps reconnecting instead
    /// of crashing the host when Redis is briefly unavailable at startup.
    /// Set <see cref="RedisStoreOptions.RequireTls"/> in <paramref name="configure"/>
    /// to enforce TLS (<c>ssl=true</c>) on the resulting connection.
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

        var hadAbortConnect = config.AbortOnConnectFail;
        var hadSsl = config.Ssl;
        if (hadAbortConnect)
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
            !hadAbortConnect, config.Ssl, options.RequireTls);

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
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services,
        IConnectionMultiplexer multiplexer,
        Action<RedisStoreOptions>? configure = null)
    {
        var options = new RedisStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(multiplexer);
        services.AddSingleton(options);
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        services.AddSingleton<IDistributedRateLimitStore, RedisRateLimitStore>();
        services.AddSingleton<Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck, RedisHealthCheck>();
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
}
