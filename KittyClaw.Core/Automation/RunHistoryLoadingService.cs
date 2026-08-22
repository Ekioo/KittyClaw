using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

public sealed record RunHistoryLoadProgress(int Loaded, int Total, bool IsComplete);

/// <summary>Loads persisted runs only after HTTP startup and reconciles each snapshot before publishing it.</summary>
public sealed class RunHistoryLoadingService(
    RunLogStore store,
    AgentRunRegistry registry,
    IHostApplicationLifetime lifetime,
    ILogger<RunHistoryLoadingService> logger) : BackgroundService
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;
    public RunHistoryLoadProgress Progress { get; private set; } = new(0, 0, false);
    public event Action<RunHistoryLoadProgress>? ProgressChanged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!lifetime.ApplicationStarted.IsCancellationRequested)
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = lifetime.ApplicationStarted.Register(started.SetResult);
                await started.Task.WaitAsync(stoppingToken);
            }

            var total = store.Count;
            Publish(0, total, false);
            logger.LogInformation("Loading {Total} persisted agent runs after HTTP startup", total);

            var loaded = 0;
            await foreach (var run in store.LoadAllAsync(stoppingToken))
            {
                // Reconcile before insertion so stale snapshots are never observable as active.
                if (run.Status == AgentRunStatus.Running)
                {
                    if (!string.IsNullOrWhiteSpace(run.ChatTarget))
                    {
                        run.Push(new StreamEvent(DateTime.UtcNow, "interrupted",
                            "KittyClaw restarted while this chat session was running."));
                    }
                    run.Status = AgentRunStatus.Stopped;
                    run.EndedAt = DateTime.UtcNow;
                    store.Save(run);
                }

                registry.Restore(run);
                loaded++;
                Publish(loaded, total, false);
            }

            Publish(loaded, total, true);
            logger.LogInformation("Loaded and reconciled {Loaded} persisted agent runs", loaded);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _completion.TrySetCanceled(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Persisted agent run history loading failed");
            _completion.TrySetException(ex);
        }
    }

    private void Publish(int loaded, int total, bool complete)
    {
        Progress = new(loaded, total, complete);
        ProgressChanged?.Invoke(Progress);
    }
}
