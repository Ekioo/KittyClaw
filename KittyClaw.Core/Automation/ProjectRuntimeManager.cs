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
            rt.Workspace = workspace;
            rt.Config = config;
            rt.Triggers = await BuildTriggersAsync(slug, config);
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
                var lastRunAt = await _triggerState.GetLastRunAtAsync(slug, a.Id) ?? DateTime.MinValue;
                trigger = new IntervalTrigger(its, lastRunAt, _triggerState, slug, a.Id);
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
