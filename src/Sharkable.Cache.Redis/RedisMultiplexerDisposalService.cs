using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Sharkable.Cache.Redis;

/// <summary>
/// Disposes the registered <see cref="IConnectionMultiplexer"/> asynchronously on
/// host stop so that in-flight commands are drained gracefully. DI's default
/// singleton disposal uses sync <see cref="IDisposable.Dispose"/> which would
/// cut off pending socket writes; the multiplexer implements
/// <see cref="IAsyncDisposable"/> and exposes a cleaner shutdown path.
/// </summary>
internal sealed class RedisMultiplexerDisposalService : IHostedService
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisMultiplexerDisposalService> _logger;

    /// <summary>
    /// Creates a new <see cref="RedisMultiplexerDisposalService"/>.
    /// </summary>
    /// <param name="multiplexer">The multiplexer to dispose on shutdown.</param>
    /// <param name="logger">Logger for shutdown diagnostics.</param>
    public RedisMultiplexerDisposalService(
        IConnectionMultiplexer multiplexer,
        ILogger<RedisMultiplexerDisposalService> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>No-op; the multiplexer is already connected at registration time.</remarks>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Calls <see cref="IAsyncDisposable.DisposeAsync"/> and logs any failure;
    /// never re-throws because shutdown must continue even if the multiplexer
    /// is already partially torn down (e.g. broken socket).
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispose IConnectionMultiplexer asynchronously during host shutdown.");
        }
    }
}
