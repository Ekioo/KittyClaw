using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly LabelService _labels;
    private readonly AutomationStore _store;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly ClaudeRunner _runner;
    private readonly CostTracker _cost;
    private readonly ILogger<AutomationEngine> _logger;

    private readonly ConcurrentDictionary<string, ProjectRuntime> _runtime = new();

    // Urgent firings produced by NotifySignal — consumed before the regular poll each tick.
    private readonly Channel<UrgentEntry> _urgentChannel =
        Channel.CreateUnbounded<UrgentEntry>(new UnboundedChannelOptions { SingleReader = true });

    private sealed record UrgentEntry(string Slug, Automation Automation, ITrigger Trigger, TriggerFiring Firing);

    public AutomationEngine(
        ProjectService projects,
        TicketService tickets,
        MemberService members,
        LabelService labels,
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
        _labels = labels;
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

        _tickets.TicketStatusChanged += (slug, ticketId, from, to) =>
            _ = NotifySignalAsync(slug, new Triggers.StatusChangeSignal(ticketId, from, to));

        _tickets.TicketCommentAdded += (slug, ticketId, author, content) =>
            _ = NotifySignalAsync(slug, new Triggers.CommentAddedSignal(ticketId, author, content));
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

    /// <summary>
    /// Push an external signal to all enabled automations of <paramref name="projectSlug"/>.
    /// Each trigger that implements <see cref="ITrigger.TryHandleExternalSignal"/> can produce
    /// firings that are enqueued and dispatched at the beginning of the very next tick (&lt;1 s).
    /// </summary>
    public async Task NotifySignalAsync(string projectSlug, object signal)
    {
        await EnsureLoadedAsync(projectSlug);
        if (!_runtime.TryGetValue(projectSlug, out var rt) || rt.Config is null) return;

        foreach (var automation in rt.Config.Automations)
        {
            if (!automation.Enabled) continue;
            if (!rt.Triggers.TryGetValue(automation.Id, out var trigger)) continue;
            if (!trigger.TryHandleExternalSignal(signal, out var firings)) continue;
            foreach (var firing in firings)
                _urgentChannel.Writer.TryWrite(new UrgentEntry(projectSlug, automation, trigger, firing));
        }
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
            _runs.PurgeOld(TimeSpan.FromHours(24));
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Drain urgent firings first (produced by NotifySignal) before the regular poll.
        while (_urgentChannel.Reader.TryRead(out var entry))
        {
            if (ct.IsCancellationRequested) return;
            var urgentProject = await _projects.GetProjectAsync(entry.Slug);
            if (urgentProject?.IsPaused == true) continue;
            await EnsureLoadedAsync(entry.Slug);
            if (!_runtime.TryGetValue(entry.Slug, out var urt) || urt.Config is null) continue;
            if (!await ConditionsMatchAsync(urt, entry.Automation, entry.Firing)) continue;
            var utctx = new TriggerContext
            {
                ProjectSlug = entry.Slug,
                WorkspacePath = urt.Workspace!,
                Automation = entry.Automation,
                Tickets = _tickets,
                Members = _members,
                Sessions = _sessions,
                Runs = _runs,
                Now = DateTime.UtcNow,
            };
            _ = ExecuteAutomationAsync(urt, entry.Automation, entry.Firing, ct, entry.Trigger, utctx);
        }

        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) return;
            if (project.IsPaused) continue;
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
                    _ = ExecuteAutomationAsync(rt, automation, firing, ct, trigger, tctx);
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
            var result = await EvaluateSingleConditionAsync(rt, cond, firing);
            if (cond.Negate) result = !result;
            if (!result) return false;
        }
        return true;
    }

    private async Task<bool> EvaluateSingleConditionAsync(ProjectRuntime rt, ConditionSpec cond, TriggerFiring firing)
    {
        switch (cond)
        {
            case TicketInColumnConditionSpec c:
                if (firing.TicketStatus is null) return false;
                return c.Columns.Count == 0 || c.Columns.Contains(firing.TicketStatus);
            case NoPendingTicketsConditionSpec c:
                var cols = c.Columns ?? new List<string> { "Todo", "InProgress" };
                // Build the set of slugs to check against.
                HashSet<string>? slugsToCheck = null;
                if (!string.IsNullOrEmpty(c.ConcurrencyGroup))
                {
                    // Collect all agentNames from runClaudeSkill actions sharing this concurrencyGroup.
                    slugsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (rt.Config is not null)
                    {
                        foreach (var auto in rt.Config.Automations)
                        foreach (var act in auto.Actions)
                        {
                            if (act is RunClaudeSkillActionSpec rcs
                                && string.Equals(rcs.ConcurrencyGroup, c.ConcurrencyGroup, StringComparison.OrdinalIgnoreCase)
                                && rcs.AgentName is not null)
                            {
                                slugsToCheck.Add(rcs.AgentName);
                            }
                        }
                    }
                }
                else if (c.AssigneeSlug is not null)
                {
                    slugsToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.AssigneeSlug };
                }
                foreach (var col in cols)
                {
                    var list = await _tickets.ListTicketsAsync(rt.Slug, statusFilter: col);
                    if (slugsToCheck is null)
                    {
                        if (list.Count > 0) return false;
                    }
                    else
                    {
                        if (list.Any(t => t.AssignedTo is not null && slugsToCheck.Contains(t.AssignedTo))) return false;
                    }
                }
                return true;
            case MinDescriptionLengthConditionSpec c:
                if (firing.TicketId is null) return true;
                var ticketMdl = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                return ticketMdl is not null && ticketMdl.Description.Length >= c.Length;
            case FieldLengthConditionSpec fl:
                if (firing.TicketId is null) return true;
                var ticketFl = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketFl is null) return false;
                var fieldValue = fl.Field == "title" ? ticketFl.Title : ticketFl.Description;
                return fl.Mode == "max" ? fieldValue.Length <= fl.Length : fieldValue.Length >= fl.Length;
            case PriorityConditionSpec pc:
                if (firing.TicketId is null) return true;
                var ticketPc = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketPc is null) return false;
                return pc.Priorities.Count == 0 || pc.Priorities.Contains(ticketPc.Priority.ToString());
            case LabelsConditionSpec lc:
                if (firing.TicketId is null) return true;
                var ticketLc = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketLc is null) return false;
                return lc.Labels.Count == 0 || ticketLc.Labels.Any(l => lc.Labels.Contains(l.Name));
            case AssignedToConditionSpec ac:
                if (firing.TicketId is null) return true;
                var ticketAc = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketAc is null) return false;
                return ac.Slugs.Count == 0
                    ? ticketAc.AssignedTo is null
                    : ac.Slugs.Contains(ticketAc.AssignedTo ?? "");
            case HasParentConditionSpec hp:
                if (firing.TicketId is null) return true;
                var ticketHp = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketHp is null) return false;
                return hp.Value ? ticketHp.ParentId is not null : ticketHp.ParentId is null;
            case TicketAgeConditionSpec ta:
                if (firing.TicketId is null) return true;
                var ticketTa = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticketTa is null) return false;
                var dateField = ta.Field == "updatedAt" ? ticketTa.UpdatedAt : ticketTa.CreatedAt;
                var age = DateTime.UtcNow - dateField;
                return ta.Mode == "newerThan" ? age.TotalHours < ta.Hours : age.TotalHours >= ta.Hours;
            default:
                return true;
        }
    }

    private async Task<AgentRun?> ExecuteAutomationAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing, CancellationToken ct, ITrigger? trigger = null, TriggerContext? tctx = null)
    {
        AgentRun? lastRun = null;
        bool committed = false;
        async Task CommitAsync()
        {
            if (committed || trigger is null || tctx is null) return;
            committed = true;
            try { await trigger.CommitFiringAsync(tctx, firing); }
            catch (Exception ex) { _logger.LogWarning(ex, "CommitFiring failed for {Id}", automation.Id); }
        }
        // Track the last moveTicketStatus so we can restore on agent failure.
        string? statusBeforeMove = null;
        string? statusAfterMove = null;
        string? assigneeBeforeMove = null;

        foreach (var action in automation.Actions)
        {
            switch (action)
            {
                case RunClaudeSkillActionSpec a:
                {
                    // Resolve placeholders in skillFile.
                    var skillFile = a.SkillFile;
                    if (skillFile.Contains('{'))
                    {
                        if (skillFile.Contains("{assignee}"))
                        {
                            if (firing.TicketId is null)
                            {
                                _logger.LogWarning("Placeholder {{assignee}} in skillFile but no ticketId in firing — skipping");
                                return null;
                            }
                            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                            var assignee = t?.AssignedTo;
                            if (string.IsNullOrEmpty(assignee))
                            {
                                _logger.LogWarning("Placeholder {{assignee}} in skillFile but ticket #{Id} has no assignee — skipping", firing.TicketId);
                                return null;
                            }
                            skillFile = skillFile.Replace("{assignee}", assignee);
                        }
                        if (firing.TicketId is not null)
                            skillFile = skillFile.Replace("{ticketId}", firing.TicketId.Value.ToString());
                        if (a.AgentName is not null)
                            skillFile = skillFile.Replace("{agent}", a.AgentName);
                    }

                    var agentName = a.AgentName ?? InferAgentName(skillFile);
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

                    // All transient gates passed — let the trigger persist its "seen" marker.
                    await CommitAsync();

                    var runCtx = new ClaudeRunContext
                    {
                        ProjectSlug = rt.Slug,
                        WorkspacePath = rt.Workspace!,
                        AgentName = agentName,
                        SkillFile = skillFile,
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

                    // Restore ticket status if agent failed/stopped and a prior moveTicketStatus changed it.
                    if (a.RestoreStatusOnFail
                        && lastRun.Status is AgentRunStatus.Failed or AgentRunStatus.Stopped
                        && statusBeforeMove is not null && statusAfterMove is not null
                        && firing.TicketId is not null)
                    {
                        try
                        {
                            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                            if (ticket is not null
                                && string.Equals(ticket.Status, statusAfterMove, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(ticket.AssignedTo ?? "", assigneeBeforeMove ?? "", StringComparison.OrdinalIgnoreCase))
                            {
                                await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId.Value, statusBeforeMove, "automation");
                                _logger.LogInformation("Restored #{Id} to {Status} (run {Agent} failed)",
                                    firing.TicketId, statusBeforeMove, agentName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to restore ticket #{Id} status", firing.TicketId);
                        }
                    }
                    break;
                }
                case MoveTicketStatusActionSpec m when firing.TicketId is not null:
                {
                    if (string.Equals(firing.TicketStatus, m.To, StringComparison.OrdinalIgnoreCase))
                        break; // already in target status
                    try
                    {
                        var ticketBefore = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                        statusBeforeMove = ticketBefore?.Status;
                        assigneeBeforeMove = ticketBefore?.AssignedTo;
                        await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId.Value, m.To, "automation");
                        statusAfterMove = m.To;
                    }
                    catch { }
                    break;
                }
                case SetLabelsActionSpec s when firing.TicketId is not null:
                {
                    try
                    {
                        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                        if (ticket is null) break;
                        var currentNames = ticket.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var name in s.Add) currentNames.Add(name);
                        foreach (var name in s.Remove) currentNames.Remove(name);
                        var allLabels = await _labels.ListLabelsAsync(rt.Slug);
                        var newIds = allLabels.Where(l => currentNames.Contains(l.Name)).Select(l => l.Id).ToList();
                        await _tickets.SetTicketLabelsAsync(rt.Slug, firing.TicketId.Value, newIds);
                    }
                    catch { }
                    break;
                }
                case AddCommentActionSpec ac when firing.TicketId is not null:
                {
                    try
                    {
                        var content = ac.Content
                            .Replace("{ticketId}", firing.TicketId?.ToString() ?? "")
                            .Replace("{ticketTitle}", firing.TicketTitle ?? "");
                        if (content.Contains("{assignee}"))
                        {
                            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                            content = content.Replace("{assignee}", ticket?.AssignedTo ?? "");
                        }
                        await _tickets.AddCommentAsync(rt.Slug, firing.TicketId.Value, content, ac.Author);
                    }
                    catch { }
                    break;
                }
                case AssignTicketActionSpec at when firing.TicketId is not null:
                {
                    try
                    {
                        var slug = at.Slug;
                        if (slug is not null && slug.Contains("{previousAssignee}"))
                        {
                            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                            slug = slug.Replace("{previousAssignee}", ticket?.AssignedTo ?? "");
                        }
                        if (string.IsNullOrEmpty(slug))
                        {
                            // unassign
                            await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId.Value, assignedTo: "", author: "automation");
                        }
                        else
                        {
                            var members = await _members.ListMembersAsync(rt.Slug);
                            if (!members.Any(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                            {
                                _logger.LogWarning("assignTicket: member '{Slug}' not found in project {Project}", slug, rt.Slug);
                                break;
                            }
                            await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId.Value, assignedTo: slug, author: "automation");
                        }
                    }
                    catch { }
                    break;
                }
            }
        }
        // No runClaudeSkill gate was hit (automation made of move/comment/label/assign only, or the
        // runClaudeSkill action was skipped due to a non-transient reason). Commit now to avoid
        // re-firing every poll.
        await CommitAsync();
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
                TicketCommentAddedTriggerSpec s => new TicketCommentAddedTrigger(s),
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
