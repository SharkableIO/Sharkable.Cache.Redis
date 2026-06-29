using Microsoft.Extensions.DependencyInjection;
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
    /// swaps the default <see cref="IIdempotencyStore"/> and
    /// <see cref="IDistributedRateLimitStore"/> for their Redis-backed
    /// implementations.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="connectionString">Redis connection string (e.g. <c>"localhost:6379"</c>).</param>
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services, string connectionString)
    {
        var multiplexer = ConnectionMultiplexer.Connect(connectionString);
        return services.AddSharkableRedis(multiplexer);
    }

    /// <summary>
    /// Uses an existing <see cref="IConnectionMultiplexer"/> and swaps
    /// the default stores for their Redis-backed implementations.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <param name="multiplexer">A pre-configured <see cref="IConnectionMultiplexer"/>.</param>
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services, IConnectionMultiplexer multiplexer)
    {
        services.AddSingleton(multiplexer);
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
    public static IServiceCollection AddSharkableRedis(
        this IServiceCollection services, ConfigurationOptions configuration)
    {
        var multiplexer = ConnectionMultiplexer.Connect(configuration);
        return services.AddSharkableRedis(multiplexer);
    }
}
