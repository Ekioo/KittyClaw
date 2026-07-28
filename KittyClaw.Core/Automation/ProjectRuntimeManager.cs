using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Owns per-project runtime state and the urgent-signal channel.
/// Handles config loading/reloading and signal fan-out to triggers.
/// </summary>
internal sealed class ProjectRuntimeManager
{
    private readonly ConcurrentDictionary<string, ProjectRuntime> _runtime = new();
    private readonly Channel<UrgentEntry> _urgentChannel =
        Channel.CreateUnbounded<UrgentEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly AutomationStore _store;
    private readonly ITriggerStateStore _triggerState;
    private readonly ILogger _logger;

    internal sealed record UrgentEntry(string Slug, Automation Automation, ITrigger Trigger, TriggerFiring Firing);

    public ProjectRuntimeManager(AutomationStore store, ITriggerStateStore triggerState, ILogger logger)
    {
        _store = store;
        _triggerState = triggerState;
        _logger = logger;
    }

    public ChannelReader<UrgentEntry> UrgentReader => _urgentChannel.Reader;

    public bool TryGetRuntime(string slug, out ProjectRuntime? rt) =>
        _runtime.TryGetValue(slug, out rt);

    public ProjectRuntime GetRuntime(string slug) => _runtime[slug];

    public async Task EnsureLoadedAsync(string slug)
    {
        var rt = _runtime.GetOrAdd(slug, s => new ProjectRuntime(s));
        if (rt.Config is null) await ReloadProjectAsync(slug);
    }

    public async Task ReloadProjectAsync(string slug)
    {
        var rt = _runtime.GetOrAdd(slug, s => new ProjectRuntime(s));
        rt.ConfigDirty = false;
        try
        {
            var (config, workspace, _) = await _store.LoadAsync(slug);
            // Build triggers BEFORE swapping anything in: if trigger construction throws, the
            // runtime keeps its previous coherent config+triggers pair instead of ending up with
            // a new config whose automations have no registered triggers (silent unregistration).
            var triggers = await BuildTriggersAsync(slug, config);
            rt.Workspace = workspace;
            rt.Config = config;
            rt.Triggers = triggers;
            _logger.LogInformation("Automations loaded for {Slug}: {Count} entries", slug, config.Automations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload automations for {Slug}", slug);
        }
    }

    public Dictionary<string, DateTime?> GetNextRunTimes(string slug)
    {
        if (!_runtime.TryGetValue(slug, out var rt) || rt.Config is null)
        {
            _ = EnsureLoadedAsync(slug);
            return new Dictionary<string, DateTime?>();
        }
        var result = new Dictionary<string, DateTime?>();
        var now = DateTime.UtcNow;
        foreach (var a in rt.Config.Automations)
        {
            if (!rt.Triggers.TryGetValue(a.Id, out var trigger)) continue;
            result[a.Id] = trigger.GetNextRunAt(now);
        }
        return result;
    }

    /// <summary>
    /// Health snapshot for GET /api/engine/health (ticket #114). Returns null when the project's
    /// config has never been loaded into the engine (which is itself a signal for the caller).
    /// </summary>
    public ProjectEngineHealth? GetProjectHealth(string slug)
    {
        if (!_runtime.TryGetValue(slug, out var rt) || rt?.Config is null) return null;
        var now = DateTime.UtcNow;
        var scheduled = 0;
        var overdue = 0;
        DateTime? nextRunAt = null;
        foreach (var a in rt.Config.Automations)
        {
            if (!a.Enabled) continue;
            // Only cron/interval triggers count as "scheduled tasks": poll-based triggers also
            // expose a next-poll time, but they recompute it each tick and cannot go overdue.
            if (!rt.Triggers.TryGetValue(a.Id, out var trigger) || trigger is not IntervalTrigger) continue;
            var next = trigger.GetNextRunAt(now);
            if (next is null) continue;
            scheduled++;
            if (nextRunAt is null || next < nextRunAt) nextRunAt = next;
            // 2-minute grace: the tick loop runs every second, so a NextRunAt sitting in the past
            // longer than that means the schedule is not being served.
            if (next < now.AddMinutes(-2)) overdue++;
        }
        return new ProjectEngineHealth(
            rt.Slug,
            rt.Config.Automations.Count,
            rt.Config.Automations.Count(a => a.Enabled),
            scheduled,
            nextRunAt,
            overdue,
            rt.LastFiredAt,
            rt.LastFiredAutomationId);
    }

    public async Task NotifySignalAsync(string slug, object signal)
    {
        await EnsureLoadedAsync(slug);
        if (!_runtime.TryGetValue(slug, out var rt) || rt.Config is null) return;

        foreach (var automation in rt.Config.Automations)
        {
            if (!automation.Enabled) continue;
            if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
            if (!trigger.TryHandleExternalSignal(signal, out var firings)) continue;
            foreach (var firing in firings)
                _urgentChannel.Writer.TryWrite(new UrgentEntry(slug, automation, trigger, firing));
        }
    }

    private async Task<Dictionary<string, ITrigger>> BuildTriggersAsync(string slug, AutomationConfig config)
    {
        var map = new Dictionary<string, ITrigger>();
        foreach (var a in config.Automations)
        {
            ITrigger trigger;
            if (a.Trigger is IntervalTriggerSpec its)
            {
                try
                {
                    var nextRunAt = await _triggerState.GetNextRunAtAsync(slug, a.Id);
                    if (nextRunAt is null)
                    {
                        // Never migrated to the NextRunAt model. Two cases:
                        //  - pre-existing install: a legacy LastRunAt is on record — anchor the seed to
                        //    it so a genuinely missed occurrence still catches up instead of silently
                        //    resetting to "next occurrence from now".
                        //  - brand-new automation: no legacy data — seed from now.
                        // Either way, persist immediately so a restart before the scheduled moment
                        // doesn't lose it and silently skip to the following occurrence.
                        var legacyLastRunAt = await _triggerState.GetLegacyLastRunAtAsync(slug, a.Id);
                        nextRunAt = IntervalTrigger.ComputeInitialNextRunAt(its, legacyLastRunAt ?? DateTime.UtcNow);
                        await _triggerState.SetNextRunAtAsync(slug, a.Id, nextRunAt.Value);
                    }
                    trigger = new IntervalTrigger(its, nextRunAt.Value, _triggerState, slug, a.Id);
                }
                catch (Exception ex)
                {
                    // A malformed spec (neither Cron nor Seconds set) must not take down every other
                    // automation in this project's reload — skip just this one.
                    _logger.LogWarning(ex, "Skipping interval trigger for automation {Id}: invalid schedule", a.Id);
                    trigger = new NullTrigger();
                }
            }
            else
            {
                trigger = a.Trigger switch
                {
                    TicketInColumnTriggerSpec t     => new TicketInColumnTrigger(t),
                    GitCommitTriggerSpec t          => new GitCommitTrigger(t),
                    StatusChangeTriggerSpec t       => new StatusChangeTrigger(t),
                    SubTicketStatusTriggerSpec t    => new SubTicketStatusTrigger(t),
                    BoardIdleTriggerSpec t          => new BoardIdleTrigger(t),
                    AgentInactivityTriggerSpec t    => new AgentInactivityTrigger(t),
                    TicketCommentAddedTriggerSpec t => new TicketCommentAddedTrigger(t),
                    _                              => new NullTrigger(),
                };
            }
            map[a.Id] = trigger;
        }
        return map;
    }

    private sealed class NullTrigger : ITrigger
    {
        public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
    }
}
