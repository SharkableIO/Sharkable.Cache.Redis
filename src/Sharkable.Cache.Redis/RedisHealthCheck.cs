using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Health check that verifies Redis connectivity via the registered
/// <see cref="IConnectionMultiplexer"/>. Automatically registered when
/// <c>AddSharkableRedis()</c> is called.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;

    /// <summary>
    /// Creates a new <see cref="RedisHealthCheck"/> backed by the given
    /// <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    public RedisHealthCheck(IConnectionMultiplexer multiplexer)
    {
        _multiplexer = multiplexer;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _multiplexer.GetDatabase();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await db.PingAsync();
            sw.Stop();

            var endpoints = _multiplexer.GetEndPoints();
            return HealthCheckResult.Healthy(
                $"Redis connected ({endpoints.Length} endpoint(s)) in {sw.ElapsedMilliseconds}ms",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = sw.ElapsedMilliseconds,
                    ["endpoints"] = endpoints.Select(e => e.ToString()).ToArray(),
                });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                $"Redis connection failed: {ex.Message}");
        }
    }
}
