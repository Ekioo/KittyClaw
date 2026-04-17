using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Todo.Core.Automation.Triggers;
using Todo.Core.Services;

namespace Todo.Core.Automation;

public sealed class AutomationEngine : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly AutomationStore _store;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly ClaudeRunner _runner;
    private readonly CostTracker _cost;
    private readonly ILogger<AutomationEngine> _logger;

    private readonly ConcurrentDictionary<string, ProjectRuntime> _runtime = new();

    public AutomationEngine(
        ProjectService projects,
        TicketService tickets,
        MemberService members,
        AutomationStore store,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        ClaudeRunner runner,
        CostTracker cost,
        ILogger<AutomationEngine> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _members = members;
        _store = store;
        _sessions = sessions;
        _runs = runs;
        _runner = runner;
        _cost = cost;
        _logger = logger;

        _store.OnConfigChangedOnDisk += slug =>
        {
            if (_runtime.TryGetValue(slug, out var rt)) rt.ConfigDirty = true;
        };
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

    public async Task<AgentRun> RunAutomationManuallyAsync(string slug, string automationId, CancellationToken ct)
    {
        await EnsureLoadedAsync(slug);
        var rt = _runtime[slug];
        var automation = rt.Config?.Automations.FirstOrDefault(a => a.Id == automationId)
            ?? throw new InvalidOperationException($"Automation '{automationId}' introuvable.");
        var firing = new TriggerFiring(null, $"manual:{automationId}", null);
        var run = await ExecuteAutomationAsync(rt, automation, firing, ct);
        return run ?? throw new InvalidOperationException("Aucun run créé (concurrence ou action invalide).");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutomationEngine started");
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
            _runs.PurgeOld(TimeSpan.FromMinutes(5));
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            await EnsureLoadedAsync(project.Slug);
            var rt = _runtime[project.Slug];
            if (rt.ConfigDirty)
            {
                // Disk changed; wait for explicit reload via API. Just log once.
                _logger.LogInformation("Config change detected on disk for {Slug} — reload requested via UI/API", project.Slug);
                rt.ConfigDirty = false; // don't spam; next real change will flag again
            }
            if (rt.Config is null) continue;
            foreach (var automation in rt.Config.Automations)
            {
                if (!automation.Enabled) continue;
                if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
                var tctx = new TriggerContext
                {
                    ProjectSlug = project.Slug,
                    WorkspacePath = rt.Workspace!,
                    Automation = automation,
                    Tickets = _tickets,
                    Members = _members,
                    Sessions = _sessions,
                    Runs = _runs,
                    Now = DateTime.UtcNow,
                };
                IReadOnlyList<TriggerFiring> firings;
                try { firings = await trigger.EvaluateAsync(tctx, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Trigger eval failed for {Id}", automation.Id); continue; }
                foreach (var firing in firings)
                {
                    if (!await ConditionsMatchAsync(rt, automation, firing)) continue;
                    _ = ExecuteAutomationAsync(rt, automation, firing, ct);
                }
            }
        }
    }

    private async Task EnsureLoadedAsync(string slug)
    {
        var rt = _runtime.GetOrAdd(slug, s => new ProjectRuntime(s));
        if (rt.Config is null) await ReloadProjectAsync(slug);
    }

    private async Task<bool> ConditionsMatchAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing)
    {
        foreach (var cond in automation.Conditions)
        {
            switch (cond)
            {
                case TicketInColumnConditionSpec c:
                    if (firing.TicketStatus is null) return false;
                    if (c.Columns.Count > 0 && !c.Columns.Contains(firing.TicketStatus)) return false;
                    break;
                case NoPendingTicketsConditionSpec c:
                    var cols = c.Columns ?? new List<string> { "Todo", "InProgress" };
                    foreach (var col in cols)
                    {
                        var list = await _tickets.ListTicketsAsync(rt.Slug, statusFilter: col);
                        if (c.AssigneeSlug is null)
                        {
                            if (list.Count > 0) return false;
                        }
                        else
                        {
                            if (list.Any(t => t.AssignedTo == c.AssigneeSlug)) return false;
                        }
                    }
                    break;
                case MinDescriptionLengthConditionSpec c:
                    // Requires full ticket fetch; skip if no ticket id in firing.
                    if (firing.TicketId is null) break;
                    var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                    if (ticket is null || ticket.Description.Length < c.Length) return false;
                    break;
            }
        }
        return true;
    }

    private async Task<AgentRun?> ExecuteAutomationAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing, CancellationToken ct)
    {
        AgentRun? lastRun = null;
        foreach (var action in automation.Actions)
        {
            switch (action)
            {
                case RunClaudeSkillActionSpec a:
                {
                    var agentName = a.AgentName ?? InferAgentName(a.SkillFile);
                    var group = string.IsNullOrEmpty(a.ConcurrencyGroup) ? agentName : a.ConcurrencyGroup;

                    // Daily budget gate (non-CEO agents only — CEO can always run to react to budget).
                    if (rt.Config?.DailyBudgetUsd is decimal cap && group != "ceo"
                        && _cost.IsBudgetExceeded(rt.Workspace!, cap))
                    {
                        _logger.LogInformation("Budget exceeded for {Slug} — skipping {Agent}", rt.Slug, agentName);
                        return null;
                    }

                    // Quality gate: min description length on targeted ticket.
                    if (rt.Config?.MinDescriptionLength is int minLen && firing.TicketId is not null)
                    {
                        var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                        if (t is not null && t.Description.Length < minLen)
                        {
                            _logger.LogInformation("Ticket #{Id} description too short ({Len}<{Min}) — skipping {Agent}",
                                firing.TicketId, t.Description.Length, minLen, agentName);
                            return null;
                        }
                    }

                    // Dedup: avoid re-dispatch on same (agent, ticket) while a run is active.
                    if (firing.TicketId is not null
                        && _runs.ActiveForTicket(rt.Slug, firing.TicketId.Value).Any(r => r.AgentName == agentName))
                        return null;

                    // Concurrency group: one run at a time (unless group is null).
                    if (_runs.HasActiveInGroup(rt.Slug, group)) return null;

                    // Mutually exclusive groups must be idle.
                    if (a.MutuallyExclusiveWith.Count > 0 && _runs.HasActiveAny(rt.Slug, a.MutuallyExclusiveWith)) return null;

                    var runCtx = new ClaudeRunContext
                    {
                        ProjectSlug = rt.Slug,
                        WorkspacePath = rt.Workspace!,
                        AgentName = agentName,
                        SkillFile = a.SkillFile,
                        TicketId = firing.TicketId,
                        TicketTitle = firing.TicketTitle,
                        TicketStatus = firing.TicketStatus,
                        MaxTurns = a.MaxTurns,
                        ConcurrencyGroup = group,
                        Env = a.Env,
                        Model = a.Model,
                        ExtraContext = a.Context,
                    };
                    _sessions.SetLastDispatched(rt.Workspace!, agentName, DateTime.UtcNow);
                    lastRun = await _runner.RunAsync(runCtx, ct);
                    break;
                }
                case MoveTicketStatusActionSpec m when firing.TicketId is not null:
                {
                    try { await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId.Value, m.To, "automation"); }
                    catch { }
                    break;
                }
            }
        }
        return lastRun;
    }

    private static string InferAgentName(string skillFile) =>
        Path.GetFileNameWithoutExtension(skillFile);

    private static Dictionary<string, ITrigger> BuildTriggers(AutomationConfig config)
    {
        var map = new Dictionary<string, ITrigger>();
        foreach (var a in config.Automations)
        {
            map[a.Id] = a.Trigger switch
            {
                IntervalTriggerSpec s => new IntervalTrigger(s),
                TicketInColumnTriggerSpec s => new TicketInColumnTrigger(s),
                GitCommitTriggerSpec s => new GitCommitTrigger(s),
                StatusChangeTriggerSpec s => new StatusChangeTrigger(s),
                SubTicketStatusTriggerSpec s => new SubTicketStatusTrigger(s),
                BoardIdleTriggerSpec s => new BoardIdleTrigger(s),
                AgentInactivityTriggerSpec s => new AgentInactivityTrigger(s),
                _ => new NullTrigger(),
            };
        }
        return map;
    }

    private sealed class ProjectRuntime
    {
        public ProjectRuntime(string slug) { Slug = slug; }
        public string Slug { get; }
        public string? Workspace { get; set; }
        public AutomationConfig? Config { get; set; }
        public Dictionary<string, ITrigger> Triggers { get; set; } = new();
        public bool ConfigDirty { get; set; }
    }

    private sealed class NullTrigger : ITrigger
    {
        public Task<IReadOnlyList<TriggerFiring>> EvaluateAsync(TriggerContext ctx, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TriggerFiring>>(Array.Empty<TriggerFiring>());
    }
}
