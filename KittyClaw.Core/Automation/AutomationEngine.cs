using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

public sealed record AutomationReloadResult(bool Success, string? Error);

public sealed class AutomationEngine : BackgroundService
{
    private readonly AgentRunRegistry _runs;
    private readonly ILogger<AutomationEngine> _logger;
    private readonly ProjectRuntimeManager _runtimeManager;
    private readonly TriggerHandler _triggerHandler;
    private readonly AutomationQueueProcessor _queueProcessor;
    private readonly SemaphoreSlim _queueWake = new(0, 1);

    public AutomationEngine(
        ProjectService projects,
        TicketService tickets,
        MemberService members,
        LabelService labels,
        AutomationStore store,
        AutomationQueueStore queue,
        TriggerStateStore triggerState,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        AgentRunner runner,
        CostTracker cost,
        LocalizationService loc,
        ILogger<AutomationEngine> logger,
        ProjectSecretVault? projectSecrets = null,
        DurableWriteRouter? durableWrites = null)
    {
        _runs = runs;
        _logger = logger;

        _runtimeManager = new ProjectRuntimeManager(store, triggerState, logger);
        var runState = new RunStateManager(runs, cost, tickets, logger);
        var executor = new ActionExecutor(tickets, members, labels, sessions, runs, runner, cost, loc, projects, runState, logger, projectSecrets, durableWrites);
        _triggerHandler = new TriggerHandler(projects, _runtimeManager, executor, tickets, members, sessions, runs, queue, loc, logger);
        _queueProcessor = new AutomationQueueProcessor(projects, tickets, queue, _runtimeManager, executor, logger);
        queue.WorkAvailable += WakeQueueConsumer;

        store.OnConfigChangedOnDisk += slug =>
        {
            if (_runtimeManager.TryGetRuntime(slug, out var rt) && rt is not null)
                rt.ConfigDirty = true;
        };

        tickets.TicketStatusChanged += (slug, ticketId, from, to) =>
            _ = NotifySignalAsync(slug, new StatusChangeSignal(ticketId, from, to));

        tickets.TicketCommentAdded += (slug, ticketId, commentId, author, content) =>
            _ = NotifySignalAsync(slug, new CommentAddedSignal(ticketId, commentId, author, content));
    }

    public Task<AutomationReloadResult> ReloadProjectAsync(string slug) => _runtimeManager.ReloadProjectAsync(slug);

    /// <summary>UTC time the engine instance was created (process start, effectively).</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>UTC time the last tick attempt finished. A stale value means the tick loop is
    /// dead or hung — the exact failure mode ticket #114 asks to make visible.</summary>
    public DateTime? LastTickAt { get; private set; }

    /// <summary>Per-project health snapshot; null if the project was never loaded into the engine.</summary>
    public ProjectEngineHealth? GetProjectHealth(string slug) => _runtimeManager.GetProjectHealth(slug);

    /// <summary>
    /// Push an external signal to all enabled automations of <paramref name="projectSlug"/>.
    /// Each trigger that implements <see cref="ITrigger.TryHandleExternalSignal"/> can produce
    /// firings that are enqueued and dispatched at the beginning of the very next tick (&lt;1 s).
    /// </summary>
    public Task NotifySignalAsync(string projectSlug, object signal) =>
        _runtimeManager.NotifySignalAsync(projectSlug, signal);

    /// <summary>
    /// Returns the next predicted UTC fire time for each automation in the project,
    /// keyed by automation ID. Returns null for event-driven triggers with no predictable schedule.
    /// Triggers a background load if the project runtime is not yet initialized.
    /// </summary>
    public Dictionary<string, DateTime?> GetNextRunTimes(string projectSlug) =>
        _runtimeManager.GetNextRunTimes(projectSlug);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutomationEngine started");
        var consumer = ConsumeQueueAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutomationEngine tick failed");
            }
            LastTickAt = DateTime.UtcNow;
            _runs.PurgeOld(TimeSpan.FromHours(24));
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        await consumer;
    }

    private async Task ConsumeQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _queueProcessor.ProcessOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Automation queue consumer tick failed"); }
            // Enqueues in this process wake the consumer immediately. The one-second fallback
            // discovers delayed retries becoming due and writes made by another process without
            // hammering every project database four times per second while the queue is empty.
            try { await _queueWake.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void WakeQueueConsumer()
    {
        try { _queueWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private Task TickAsync(CancellationToken ct) => _triggerHandler.ProcessTickAsync(ct);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var active = _runs.AllActive().ToArray();
        foreach (var run in active) run.Cancellation.Cancel();
        await base.StopAsync(cancellationToken);
        await WaitForRunsAsync(active, cancellationToken);
    }

    public static Task CancelAndWaitForRunsAsync(IEnumerable<AgentRun> runs, CancellationToken ct)
    {
        var snapshot = runs.ToArray();
        foreach (var run in snapshot) run.Cancellation.Cancel();
        return WaitForRunsAsync(snapshot, ct);
    }

    public static async Task WaitForRunsAsync(IEnumerable<AgentRun> runs, CancellationToken ct)
    {
        var snapshot = runs.ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (snapshot.Any(run => run.Status == AgentRunStatus.Running) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        var remaining = snapshot.Where(run => run.Status == AgentRunStatus.Running).Select(run => run.RunId).ToArray();
        if (remaining.Length > 0)
            throw new TimeoutException($"Run cleanup did not finish within 10 seconds: {string.Join(", ", remaining)}");
    }
}
