using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Health check that verifies Redis connectivity via the registered
/// <see cref="IConnectionMultiplexer"/>. The status returned to <c>/healthz</c>
/// is intentionally generic; topology and exception details are logged for
/// operators only. To surface this check on the public <c>/healthz</c>
/// endpoint, call <c>UseSharkableRedisHealthCheck()</c> explicitly.
/// </summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisHealthCheck> _logger;

    /// <summary>
    /// Creates a new <see cref="RedisHealthCheck"/> backed by the given
    /// <see cref="IConnectionMultiplexer"/>.
    /// </summary>
    /// <param name="multiplexer">The Redis connection multiplexer.</param>
    /// <param name="logger">Logger for diagnostic details (topology, exceptions).</param>
    public RedisHealthCheck(IConnectionMultiplexer multiplexer, ILogger<RedisHealthCheck> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns generic status to public <c>/healthz</c>; see logs for details.
    /// The healthy payload exposes only a latency measurement and an endpoint
    /// count — never addresses, host names, or exception messages.
    /// </remarks>
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

            var endpointCount = _multiplexer.GetEndPoints().Length;
            return HealthCheckResult.Healthy(
                "Redis reachable",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = sw.ElapsedMilliseconds,
                    ["endpointCount"] = endpointCount,
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis health check failed.");
            return HealthCheckResult.Unhealthy("Redis unreachable");
        }
    }
}
