using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Sharkable.Cache.Redis.HealthChecks;

/// <summary>
/// Extension methods for wiring <see cref="RedisHealthCheck"/> into the
/// ASP.NET Core <c>HealthCheckService</c> and thus into the public <c>/healthz</c>
/// endpoint. Consumers must call <c>UseSharkableRedisHealthCheck()</c>
/// explicitly — the check is not auto-surfaced.
/// </summary>
public static class RedisHealthCheckExtensions
{
    /// <summary>
    /// Registers <see cref="RedisHealthCheck"/> with the
    /// <see cref="HealthCheckService"/> under the name <c>"redis"</c> and tag
    /// <c>"ready"</c> (so the readiness probe can filter on it).
    /// Call this after <c>AddSharkableRedis()</c> if you want the check to
    /// appear on <c>/healthz</c>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/>.</param>
    /// <returns>The <see cref="IHealthChecksBuilder"/> for further chaining.</returns>
    public static IHealthChecksBuilder UseSharkableRedisHealthCheck(
        this IServiceCollection services)
    {
        return services.AddHealthChecks().AddCheck<RedisHealthCheck>(
            "redis",
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
    }
}