using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

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
    private readonly LocalizationService _loc;
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
        LocalizationService loc,
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
        _loc = loc;
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
            await ExecuteAutomationAsync(urt, entry.Automation, entry.Firing, ct, entry.Trigger, utctx);
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
                    // Awaited: the prep phase (conditions, moves, gates) runs to completion, reserving
                    // DB state and concurrency slots before we evaluate the next firing. The actual
                    // long-running subprocess is fire-and-forget inside ExecuteRunAgentActionAsync.
                    await ExecuteAutomationAsync(rt, automation, firing, ct, trigger, tctx);
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

    private Task<bool> EvaluateSingleConditionAsync(ProjectRuntime rt, ConditionSpec cond, TriggerFiring firing) =>
        cond switch
        {
            TicketInColumnConditionSpec c         => Task.FromResult(EvaluateTicketInColumn(c, firing)),
            MinDescriptionLengthConditionSpec c    => EvaluateMinDescriptionLengthAsync(rt, c, firing),
            FieldLengthConditionSpec c             => EvaluateFieldLengthAsync(rt, c, firing),
            PriorityConditionSpec c                => EvaluatePriorityAsync(rt, c, firing),
            LabelsConditionSpec c                  => EvaluateLabelsAsync(rt, c, firing),
            AssignedToConditionSpec c              => EvaluateAssignedToAsync(rt, c, firing),
            HasParentConditionSpec c               => EvaluateHasParentAsync(rt, c, firing),
            AllSubTicketsInStatusConditionSpec c   => EvaluateAllSubTicketsInStatusAsync(rt, c, firing),
            TicketCountInColumnConditionSpec c     => EvaluateTicketCountInColumnAsync(rt, c, firing),
            TicketAgeConditionSpec c               => EvaluateTicketAgeAsync(rt, c, firing),
            _                                      => Task.FromResult(true),
        };

    private static bool EvaluateTicketInColumn(TicketInColumnConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketStatus is null) return false;
        return c.Columns.Count == 0 || c.Columns.Contains(firing.TicketStatus);
    }

    private async Task<bool> EvaluateMinDescriptionLengthAsync(ProjectRuntime rt, MinDescriptionLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        return ticket is not null && ticket.Description.Length >= c.Length;
    }

    private async Task<bool> EvaluateFieldLengthAsync(ProjectRuntime rt, FieldLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        var value = c.Field == "title" ? ticket.Title : ticket.Description;
        return c.Mode == "max" ? value.Length <= c.Length : value.Length >= c.Length;
    }

    private async Task<bool> EvaluatePriorityAsync(ProjectRuntime rt, PriorityConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return c.Priorities.Count == 0 || c.Priorities.Contains(ticket.Priority.ToString());
    }

    private async Task<bool> EvaluateLabelsAsync(ProjectRuntime rt, LabelsConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return c.Labels.Count == 0 || ticket.Labels.Any(l => c.Labels.Contains(l.Name));
    }

    private async Task<bool> EvaluateAssignedToAsync(ProjectRuntime rt, AssignedToConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return c.Slugs.Count == 0
            ? ticket.AssignedTo is null
            : c.Slugs.Contains(ticket.AssignedTo ?? "");
    }

    private async Task<bool> EvaluateHasParentAsync(ProjectRuntime rt, HasParentConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return c.Value ? ticket.ParentId is not null : ticket.ParentId is null;
    }

    private async Task<bool> EvaluateAllSubTicketsInStatusAsync(ProjectRuntime rt, AllSubTicketsInStatusConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null || ticket.SubTickets.Count == 0) return false;
        return ticket.SubTickets.All(s => c.Statuses.Contains(s.Status));
    }

    private async Task<bool> EvaluateTicketCountInColumnAsync(ProjectRuntime rt, TicketCountInColumnConditionSpec c, TriggerFiring firing)
    {
        string? slug = c.AssigneeSlug;
        if (c.SameAssignee)
        {
            if (firing.TicketId is null) return false;
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            slug = ticket?.AssignedTo;
            if (string.IsNullOrEmpty(slug)) return false;
        }

        var cols = c.Columns.Count > 0 ? c.Columns : new List<string> { "Todo", "InProgress" };
        int count = 0;
        foreach (var col in cols)
        {
            var list = await _tickets.ListTicketsAsync(rt.Slug, statusFilter: col);
            count += string.IsNullOrEmpty(slug) ? list.Count : list.Count(t => t.AssignedTo == slug);
        }

        return c.Operator switch
        {
            "=="  => count == c.Value,
            "!="  => count != c.Value,
            "<"   => count < c.Value,
            "<="  => count <= c.Value,
            ">"   => count > c.Value,
            ">="  => count >= c.Value,
            _     => false,
        };
    }

    private async Task<bool> EvaluateTicketAgeAsync(ProjectRuntime rt, TicketAgeConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        var dateField = c.Field == "updatedAt" ? ticket.UpdatedAt : ticket.CreatedAt;
        var age = DateTime.UtcNow - dateField;
        return c.Mode == "newerThan" ? age.TotalHours < c.Hours : age.TotalHours >= c.Hours;
    }

    private sealed class ActionState
    {
        public AgentRun? LastRun;
        public string? StatusBeforeMove;
        public string? StatusAfterMove;
        public string? AssigneeBeforeMove;
    }

    private async Task<AgentRun?> ExecuteAutomationAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing, CancellationToken ct, ITrigger? trigger = null, TriggerContext? tctx = null)
    {
        var state = new ActionState();
        bool committed = false;
        async Task CommitAsync()
        {
            if (committed || trigger is null || tctx is null) return;
            committed = true;
            try { await trigger.CommitFiringAsync(tctx, firing); }
            catch (Exception ex) { _logger.LogWarning(ex, "CommitFiring failed for {Id}", automation.Id); }
        }

        for (int i = 0; i < automation.Actions.Count; i++)
        {
            var action = automation.Actions[i];
            switch (action)
            {
                case RunAgentActionSpec a:
                {
                    var remaining = automation.Actions.Skip(i + 1).ToList();
                    var skip = await ExecuteRunAgentActionAsync(rt, automation, firing, a, ct, CommitAsync, state, remaining);
                    // Whether skipped or dispatched, remaining actions are NOT processed here:
                    // skipped → they'd be wrong without the run; dispatched → the continuation handles them.
                    if (skip) return null;
                    return state.LastRun;
                }
                case MoveTicketStatusActionSpec m when firing.TicketId is not null:
                    await ExecuteMoveTicketStatusActionAsync(rt, firing, m, state);
                    break;
                case SetLabelsActionSpec s when firing.TicketId is not null:
                    await ExecuteSetLabelsActionAsync(rt, firing, s);
                    break;
                case AddCommentActionSpec ac when firing.TicketId is not null:
                    await ExecuteAddCommentActionAsync(rt, firing, ac);
                    break;
                case AssignTicketActionSpec at when firing.TicketId is not null:
                    await ExecuteAssignTicketActionAsync(rt, firing, at);
                    break;
                case CommitAgentMemoryActionSpec cm:
                    await ExecuteCommitAgentMemoryActionAsync(rt, cm, firing);
                    break;
                case ExecutePowerShellActionSpec ps:
                {
                    var abort = await ExecutePowerShellAsync(ps, rt.Workspace!, ct);
                    if (abort) return state.LastRun;
                    break;
                }
                case CreateTicketActionSpec cta:
                    await ExecuteCreateTicketActionAsync(rt, cta);
                    break;
            }
        }
        // No runClaudeSkill gate was hit (automation made of move/comment/label/assign only, or the
        // runClaudeSkill action was skipped due to a non-transient reason). Commit now to avoid
        // re-firing every poll.
        await CommitAsync();
        return state.LastRun;
    }

    // Returns true when the caller should abort and return null (gate not passed).
    // When false, the run has been DISPATCHED (not awaited): the caller must NOT continue processing
    // actions after this one — the continuation will handle <paramref name="remainingActions"/>.
    private async Task<bool> ExecuteRunAgentActionAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing, RunAgentActionSpec a, CancellationToken ct, Func<Task> commitAsync, ActionState state, List<ActionSpec> remainingActions)
    {
        // Resolve {assignee} placeholder in Agent field (delegation pattern).
        var agentName = a.Agent;
        if (agentName.Contains("{assignee}"))
        {
            if (firing.TicketId is null)
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but no ticketId in firing — skipping");
                return true;
            }
            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            var assignee = t?.AssignedTo;
            if (string.IsNullOrEmpty(assignee))
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but ticket #{Id} has no assignee — skipping", firing.TicketId);
                return true;
            }
            agentName = agentName.Replace("{assignee}", assignee);
        }

        // Resolve skill file by convention: .agents/{agent}/SKILL.md
        var skillFile = $"{agentName}/SKILL.md";
        // Resolve {assignee} placeholder in concurrency group too (for generic dispatch pattern).
        var group = string.IsNullOrEmpty(a.ConcurrencyGroup) ? agentName : a.ConcurrencyGroup.Replace("{assignee}", agentName);

        // Daily budget gate (non-CEO agents only — CEO can always run to react to budget).
        if (rt.Config?.DailyBudgetUsd is decimal cap && group != "ceo"
            && _cost.IsBudgetExceeded(rt.Workspace!, cap))
        {
            _logger.LogInformation("Budget exceeded for {Slug} — skipping {Agent}", rt.Slug, agentName);
            return true;
        }

        // Quality gate: min description length on targeted ticket.
        if (rt.Config?.MinDescriptionLength is int minLen && firing.TicketId is not null)
        {
            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            if (t is not null && t.Description.Length < minLen)
            {
                _logger.LogInformation("Ticket #{Id} description too short ({Len}<{Min}) — skipping {Agent}",
                    firing.TicketId, t.Description.Length, minLen, agentName);
                return true;
            }
        }

        // Dedup: avoid re-dispatch on same (agent, ticket) while a run is active.
        if (firing.TicketId is not null
            && _runs.ActiveForTicket(rt.Slug, firing.TicketId.Value).Any(r => r.AgentName == agentName))
            return true;

        // Concurrency group: one run at a time (unless group is null).
        if (_runs.HasActiveInGroup(rt.Slug, group)) return true;

        // Mutually exclusive groups must be idle.
        if (a.MutuallyExclusiveWith.Count > 0 && _runs.HasActiveAny(rt.Slug, a.MutuallyExclusiveWith)) return true;

        // All transient gates passed — let the trigger persist its "seen" marker.
        await commitAsync();

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
        if (firing.TicketId is not null)
        {
            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get("ActAgentStarted", agentName), "automation"); }
            catch { /* non-blocking */ }
        }

        // Start the run. _runner.RunAsync registers the run in _runs synchronously (before its
        // first await), so the concurrency group is reserved by the time this method returns —
        // meaning the NEXT firing's condition checks will see the group as busy. Do NOT await
        // the full task; schedule the post-run continuation to run remaining actions.
        var runTask = _runner.RunAsync(runCtx, ct);
        var statusBefore = state.StatusBeforeMove;
        var statusAfter = state.StatusAfterMove;
        var assigneeBefore = state.AssigneeBeforeMove;
        _ = HandleRunCompletionAsync(runTask, rt, firing, a, agentName, statusBefore, statusAfter, assigneeBefore, remainingActions, ct);
        state.LastRun = null;
        return false;
    }

    private async Task HandleRunCompletionAsync(
        Task<AgentRun> runTask,
        ProjectRuntime rt,
        TriggerFiring firing,
        RunAgentActionSpec spec,
        string agentName,
        string? statusBeforeMove,
        string? statusAfterMove,
        string? assigneeBeforeMove,
        List<ActionSpec> remainingActions,
        CancellationToken ct)
    {
        AgentRun run;
        try { run = await runTask; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "runAgent {Agent} crashed for ticket #{Id}", agentName, firing.TicketId);
            return;
        }

        if (firing.TicketId is not null)
        {
            var statusKey = run.Status switch
            {
                AgentRunStatus.Completed => "ActAgentCompleted",
                AgentRunStatus.Failed    => "ActAgentFailed",
                AgentRunStatus.Stopped   => "ActAgentStopped",
                _                        => "ActAgentCompleted",
            };
            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get(statusKey, agentName), "automation"); }
            catch { /* non-blocking */ }
        }

        if (spec.RestoreStatusOnFail
            && run.Status is AgentRunStatus.Failed or AgentRunStatus.Stopped
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

        // Process remaining actions (typically commitAgentMemory) that depend on the run having finished.
        foreach (var post in remainingActions)
        {
            try
            {
                switch (post)
                {
                    case CommitAgentMemoryActionSpec cm: await ExecuteCommitAgentMemoryActionAsync(rt, cm, firing); break;
                    case AddCommentActionSpec ac when firing.TicketId is not null: await ExecuteAddCommentActionAsync(rt, firing, ac); break;
                    case SetLabelsActionSpec sl when firing.TicketId is not null: await ExecuteSetLabelsActionAsync(rt, firing, sl); break;
                    case AssignTicketActionSpec at when firing.TicketId is not null: await ExecuteAssignTicketActionAsync(rt, firing, at); break;
                    case ExecutePowerShellActionSpec ps: await ExecutePowerShellAsync(ps, rt.Workspace!, ct); break;
                    // A second RunAgent post-run is not supported — skip silently.
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Post-run action {Type} failed", post.GetType().Name); }
        }
    }

    private async Task ExecuteMoveTicketStatusActionAsync(ProjectRuntime rt, TriggerFiring firing, MoveTicketStatusActionSpec m, ActionState state)
    {
        if (string.Equals(firing.TicketStatus, m.To, StringComparison.OrdinalIgnoreCase))
            return; // already in target status
        try
        {
            var ticketBefore = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            state.StatusBeforeMove = ticketBefore?.Status;
            state.AssigneeBeforeMove = ticketBefore?.AssignedTo;
            await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId!.Value, m.To, "automation");
            state.StatusAfterMove = m.To;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "moveTicketStatus failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteSetLabelsActionAsync(ProjectRuntime rt, TriggerFiring firing, SetLabelsActionSpec s)
    {
        try
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
            if (ticket is null) return;
            var currentNames = ticket.Labels.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in s.Add) currentNames.Add(name);
            foreach (var name in s.Remove) currentNames.Remove(name);
            var allLabels = await _labels.ListLabelsAsync(rt.Slug);
            var newIds = allLabels.Where(l => currentNames.Contains(l.Name)).Select(l => l.Id).ToList();
            await _tickets.SetTicketLabelsAsync(rt.Slug, firing.TicketId!.Value, newIds);
            var parts = new List<string>();
            if (s.Add.Count > 0) parts.Add(_loc.Get("ActLabelsAdded", string.Join(", ", s.Add)));
            if (s.Remove.Count > 0) parts.Add(_loc.Get("ActLabelsRemoved", string.Join(", ", s.Remove)));
            if (parts.Count > 0)
                try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId!.Value, _loc.Get("ActLabelsChanged", string.Join(" / ", parts)), "automation"); }
                catch { /* non-blocking */ }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "setLabels failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteAddCommentActionAsync(ProjectRuntime rt, TriggerFiring firing, AddCommentActionSpec ac)
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
            await _tickets.AddCommentAsync(rt.Slug, firing.TicketId!.Value, content, ac.Author);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "addComment failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    private async Task ExecuteAssignTicketActionAsync(ProjectRuntime rt, TriggerFiring firing, AssignTicketActionSpec at)
    {
        try
        {
            var slug = at.Slug;
            if (slug is not null && slug.Contains("{previousAssignee}"))
            {
                var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId!.Value);
                slug = slug.Replace("{previousAssignee}", ticket?.AssignedTo ?? "");
            }
            if (string.IsNullOrEmpty(slug))
            {
                // unassign
                await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: "", author: "automation");
            }
            else
            {
                var members = await _members.ListMembersAsync(rt.Slug);
                if (!members.Any(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("assignTicket: member '{Slug}' not found in project {Project}", slug, rt.Slug);
                    return;
                }
                await _tickets.UpdateTicketAsync(rt.Slug, firing.TicketId!.Value, assignedTo: slug, author: "automation");
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "assignTicket failed for ticket #{Id} in project {Project}", firing.TicketId, rt.Slug); }
    }

    // Serializes in-process git operations (commitAgentMemory runs across multiple automations).
    // External git activity (committer agent's claude CLI, owner commits) is outside this lock —
    // we handle those races by retrying on index.lock transient failures.
    private static readonly SemaphoreSlim _gitLock = new(1, 1);

    private async Task ExecuteCreateTicketActionAsync(ProjectRuntime rt, CreateTicketActionSpec cta)
    {
        try
        {
            var today = DateTime.Today;
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var firstOfMonth = new DateTime(today.Year, today.Month, 1);
            string Resolve(string s) => s
                .Replace("{date}", today.ToString("yyyy-MM-dd"))
                .Replace("{monday}", monday.ToString("yyyy-MM-dd"))
                .Replace("{firstOfMonth}", firstOfMonth.ToString("yyyy-MM-dd"));

            var title = Resolve(cta.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.LogWarning("createTicket: resolved title is empty — skipping");
                return;
            }

            if (cta.SkipIfExists)
            {
                var existing = await _tickets.ListTicketsAsync(rt.Slug);
                var openStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Backlog", "Todo", "InProgress", "Blocked", "Review" };
                if (existing.Any(t => openStatuses.Contains(t.Status) && string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("createTicket: open ticket with title '{Title}' already exists — skipping", title);
                    return;
                }
            }

            List<int>? labelIds = null;
            if (cta.Labels.Count > 0)
            {
                var allLabels = await _labels.ListLabelsAsync(rt.Slug);
                labelIds = allLabels
                    .Where(l => cta.Labels.Any(n => string.Equals(n, l.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(l => l.Id)
                    .ToList();
            }

            var priority = Enum.TryParse<KittyClaw.Core.Models.TicketPriority>(cta.Priority, ignoreCase: true, out var p)
                ? p : KittyClaw.Core.Models.TicketPriority.NiceToHave;

            var ticket = await _tickets.CreateTicketAsync(
                rt.Slug,
                title,
                description: Resolve(cta.Description),
                createdBy: string.IsNullOrWhiteSpace(cta.CreatedBy) ? "automation" : cta.CreatedBy,
                status: cta.Status,
                labelIds: labelIds,
                priority: priority,
                assignedTo: string.IsNullOrWhiteSpace(cta.AssignedTo) ? null : cta.AssignedTo,
                parentId: cta.ParentId);

            _logger.LogInformation("createTicket: created ticket #{Id} '{Title}' in project {Project}", ticket.Id, ticket.Title, rt.Slug);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "createTicket failed in project {Project}", rt.Slug); }
    }

    private async Task ExecuteCommitAgentMemoryActionAsync(ProjectRuntime rt, CommitAgentMemoryActionSpec cm, TriggerFiring? firing = null)
    {
        try
        {
            // Resolve {assignee} placeholder (same pattern as RunAgent).
            var agent = cm.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    _logger.LogInformation("commitAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return;
                }
                var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    _logger.LogInformation("commitAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping", firing.TicketId);
                    return;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            var workspace = rt.Workspace!;
            var memoryRel = $".agents/{agent}/memory.md";
            var memoryAbs = Path.Combine(workspace, memoryRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(memoryAbs))
            {
                _logger.LogInformation("commitAgentMemory: no memory file found for {Agent} at {Path}", agent, memoryAbs);
                return;
            }

            // Only proceed if we're inside a git repo.
            if (!Directory.Exists(Path.Combine(workspace, ".git")))
            {
                _logger.LogDebug("commitAgentMemory: workspace {Path} is not a git repo — skipping", workspace);
                return;
            }

            await _gitLock.WaitAsync();
            try
            {
                // Is the memory file dirty? (exit 0 = no changes, exit 1 = changes, >1 = error)
                var diff = await RunGitAsync(workspace, $"diff --quiet --exit-code -- \"{memoryRel}\"");
                if (diff.exitCode == 0)
                {
                    _logger.LogDebug("commitAgentMemory: {Agent} memory is clean, nothing to commit", agent);
                    return;
                }

                var add = await RunGitAsync(workspace, $"add -- \"{memoryRel}\"");
                if (add.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git add failed for {Agent}: {Err}", agent, add.stderr);
                    return;
                }

                var ticketSuffix = firing?.TicketId is int tid ? $" (#{tid})" : "";
                var msg = $"chore(memory): {agent}{ticketSuffix}";
                var commit = await RunGitAsync(workspace, $"commit --no-verify -m \"{msg}\" -- \"{memoryRel}\"");
                if (commit.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git commit failed for {Agent}: {Err}", agent, commit.stderr);
                    return;
                }

                var lineCount = (await File.ReadAllTextAsync(memoryAbs)).Split('\n').Length;
                _logger.LogInformation("commitAgentMemory: committed {Agent} memory ({Lines} lines)", agent, lineCount);
            }
            finally { _gitLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "commitAgentMemory: failed to commit memory for {Agent}", cm.Agent);
        }
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string cwd, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    // Returns true when AbortOnFailure is set and the process exited with a non-zero code.
    private async Task<bool> ExecutePowerShellAsync(ExecutePowerShellActionSpec spec, string workspacePath, CancellationToken ct)
    {
        try
        {
            string scriptArg;
            if (!string.IsNullOrWhiteSpace(spec.ScriptFile))
            {
                var path = Path.IsPathRooted(spec.ScriptFile)
                    ? spec.ScriptFile
                    : Path.Combine(workspacePath, spec.ScriptFile);
                scriptArg = $"-File \"{path}\"";
            }
            else
            {
                // Encode inline script as Base64 to avoid quoting issues.
                var bytes = System.Text.Encoding.Unicode.GetBytes(spec.Script);
                scriptArg = $"-EncodedCommand {Convert.ToBase64String(bytes)}";
            }

            var extraArgs = spec.Arguments.Count > 0
                ? " -Args " + string.Join(",", spec.Arguments.Select(a => $"\"{a}\""))
                : "";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NonInteractive -NoProfile {scriptArg}{extraArgs}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspacePath,
            };
            foreach (var (k, v) in spec.Env)
                psi.Environment[k] = v;

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start pwsh process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(spec.TimeoutSeconds));

            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var exitCode = proc.ExitCode;
            _logger.LogInformation("executePowerShell exited {Code}. stdout={Stdout} stderr={Stderr}",
                exitCode, stdout.Trim(), stderr.Trim());

            if (exitCode != 0)
            {
                _logger.LogWarning("executePowerShell non-zero exit ({Code}); abortOnFailure={Abort}", exitCode, spec.AbortOnFailure);
                return spec.AbortOnFailure;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("executePowerShell timed out after {Timeout}s", spec.TimeoutSeconds);
            if (spec.AbortOnFailure) return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "executePowerShell failed");
            if (spec.AbortOnFailure) return true;
        }
        return false;
    }

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
