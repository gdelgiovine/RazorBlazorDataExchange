using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Periodically removes inactive exchange session partitions.
/// </summary>
internal sealed class RazorBlazorDataExchangeCleanupService : BackgroundService
{
    private readonly RazorBlazorDataExchange _exchange;
    private readonly RazorBlazorDataExchangeOptions _options;
    private readonly ILogger<RazorBlazorDataExchangeCleanupService> _logger;

    public RazorBlazorDataExchangeCleanupService(
        RazorBlazorDataExchange exchange,
        IOptions<RazorBlazorDataExchangeOptions> options,
        ILogger<RazorBlazorDataExchangeCleanupService> logger)
    {
        _exchange = exchange;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CleanupInterval <= TimeSpan.Zero)
        {
            _logger.LogDebug("Razor/Blazor exchange automatic cleanup is disabled because CleanupInterval is not positive.");
            return;
        }

        using var timer = new PeriodicTimer(_options.CleanupInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _exchange.CleanupInactiveSessions(_options.SessionIdleTimeout);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
