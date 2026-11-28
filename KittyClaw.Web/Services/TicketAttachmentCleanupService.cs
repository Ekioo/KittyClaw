using KittyClaw.Core.Services;

namespace KittyClaw.Web.Services;

public sealed class TicketAttachmentCleanupService(TicketAttachmentService attachments,
    ILogger<TicketAttachmentCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await attachments.CleanupAsync(stoppingToken);
                    if (result.Aborted) logger.LogWarning("Attachment cleanup skipped: a complete reference scan was unavailable.");
                    else if (result.RemovedFiles > 0 || result.DeferredFiles > 0)
                        logger.LogInformation("Attachment cleanup removed {Count} files ({Bytes} bytes); {Deferred} files deferred.", result.RemovedFiles, result.FreedBytes, result.DeferredFiles);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogWarning("Attachment cleanup failed and will retry at the next interval."); }
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
