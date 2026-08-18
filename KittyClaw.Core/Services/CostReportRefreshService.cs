using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Services;

/// <summary>Refreshes the durable cost snapshot away from the UI request path.</summary>
public sealed class CostReportRefreshService(
    CostReportService reports,
    ILogger<CostReportRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Kestrel start listening before the first historical scan begins.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await reports.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh the durable cost report snapshot");
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var requested = reports.WaitForRefreshRequestAsync(waitCancellation.Token).AsTask();
            var periodic = Task.Delay(RefreshInterval, waitCancellation.Token);
            await Task.WhenAny(requested, periodic);
            waitCancellation.Cancel();
            try { await Task.WhenAll(requested, periodic); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
        }
    }
}
