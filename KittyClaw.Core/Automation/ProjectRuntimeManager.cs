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
    private readonly ILogger _logger;

    internal sealed record UrgentEntry(string Slug, Automation Automation, ITrigger Trigger, TriggerFiring Firing);

    public ProjectRuntimeManager(AutomationStore store, ILogger logger)
    {
        _store = store;
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
            rt.Triggers = BuildTriggers(config);
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

    private static Dictionary<string, ITrigger> BuildTriggers(AutomationConfig config)
    {
        var map = new Dictionary<string, ITrigger>();
        foreach (var a in config.Automations)
        {
            map[a.Id] = a.Trigger switch
            {
                IntervalTriggerSpec s          => new IntervalTrigger(s),
                TicketInColumnTriggerSpec s     => new TicketInColumnTrigger(s),
                GitCommitTriggerSpec s          => new GitCommitTrigger(s),
                StatusChangeTriggerSpec s       => new StatusChangeTrigger(s),
                SubTicketStatusTriggerSpec s    => new SubTicketStatusTrigger(s),
                BoardIdleTriggerSpec s          => new BoardIdleTrigger(s),
                AgentInactivityTriggerSpec s    => new AgentInactivityTrigger(s),
                TicketCommentAddedTriggerSpec s => new TicketCommentAddedTrigger(s),
                _                              => new NullTrigger(),
            };
        }
        return map;
    }

    private sealed class NullTrigger : ITrigger
    {
        public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
    }
}
