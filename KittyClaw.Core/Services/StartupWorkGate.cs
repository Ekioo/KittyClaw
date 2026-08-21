using Microsoft.Extensions.Hosting;

namespace KittyClaw.Core.Services;

/// <summary>Defers expensive maintenance until the host has finished starting.</summary>
public class StartupWorkGate(IHostApplicationLifetime lifetime)
{
    public virtual Task WaitAsync(CancellationToken cancellationToken) =>
        lifetime.ApplicationStarted.IsCancellationRequested
            ? Task.CompletedTask
            : WaitForApplicationStartedAsync(lifetime.ApplicationStarted, cancellationToken);

    private static async Task WaitForApplicationStartedAsync(
        CancellationToken applicationStarted,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            applicationStarted, cancellationToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
        catch (OperationCanceledException) when (applicationStarted.IsCancellationRequested)
        {
        }
    }
}
