using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Sharkable;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Redis cache plugin — auto-discovered by Sharkable at startup.
/// Partners with <c>services.AddSharkableRedis("localhost:6379")</c>
/// called before <c>AddShark()</c> to register Redis-backed stores
/// for rate limiting, idempotency, saga, and cron jobs.
/// </summary>
public sealed class RedisCachePlugin : ISharkPlugin
{
    /// <inheritdoc />
    public string Name => "Sharkable.Cache.Redis";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, SharkOption option)
    {
        // Service registration is handled by AddSharkableRedis() called before AddShark().
        // The plugin's role is discovery — Sharkable core finds this implementation
        // and knows the Redis package is available in the deployment.
    }

    /// <inheritdoc />
    public void ConfigurePipeline(WebApplication app, SharkOption option)
    {
        // No pipeline modifications needed.
    }

    /// <inheritdoc />
    public void ConfigureOpenApi(OpenApiOptions openApiOptions, SharkOption option)
    {
        // No additional OpenAPI transforms needed.
    }
}
