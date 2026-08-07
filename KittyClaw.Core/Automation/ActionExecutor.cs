using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Evaluates automation conditions and executes action sequences.
/// Delegates individual action types to focused handler classes; owns chain orchestration
/// and the in-flight chain/detached-action guards.
/// </summary>
internal sealed class ActionExecutor
{
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly AgentRunner _runner;
    private readonly CostTracker _cost;
    private readonly LocalizationService _loc;
    private readonly ProjectService _projects;
    private readonly RunStateManager _runState;
    private readonly ILogger _logger;

    private readonly TicketMutationHandler _ticketMutation;
    private readonly AgentMemoryHandler _agentMemory;
    private readonly NetworkActionHandler _network;

    // Tracks in-flight action chains keyed by "{automationId}:{ticketId}".
    // Prevents concurrent chains for the same (automation, ticket) pair.
    private readonly ConcurrentDictionary<string, byte> _inFlightChains = new();

    // Long-running actions are detached so the engine tick stays responsive. Ticket-independent
    // actions (for example a board-wide delivery script) are coalesced per project/automation:
    // one status transition per ticket must not fan out into hundreds of identical processes.
    private readonly ConcurrentDictionary<string, byte> _inFlightDetachedActions = new();

    public ActionExecutor(
        TicketService tickets,
        MemberService members,
        LabelService labels,
        SessionRegistry sessions,
        AgentRunRegistry runs,
        AgentRunner runner,
        CostTracker cost,
        LocalizationService loc,
        ProjectService projects,
        RunStateManager runState,
        ILogger logger)
    {
        _tickets = tickets;
        _members = members;
        _sessions = sessions;
        _runs = runs;
        _runner = runner;
        _cost = cost;
        _loc = loc;
        _projects = projects;
        _runState = runState;
        _logger = logger;

        _ticketMutation = new TicketMutationHandler(tickets, labels, members, loc, logger);
        _agentMemory = new AgentMemoryHandler(tickets, members, projects, runner, sessions, logger);
        _network = new NetworkActionHandler(tickets, logger);
    }

    // ── Condition evaluation ────────────────────────────────────────────────

    public async Task<bool> ConditionsMatchAsync(ProjectRuntime rt, Automation automation, TriggerFiring firing)
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
            TicketInColumnConditionSpec c         => EvaluateTicketInColumnAsync(rt, c, firing),
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

    // Signal-path firings (TryHandleExternalSignal, e.g. ticketCommentAdded) carry only the
    // ticket id — no status. Without a live lookup the condition evaluates against null and
    // ALWAYS fails, so event-driven automations with a column condition could only ever fire
    // via the slow poll path, silently (ticket #135).
    private async Task<bool> EvaluateTicketInColumnAsync(ProjectRuntime rt, TicketInColumnConditionSpec c, TriggerFiring firing)
    {
        var status = firing.TicketStatus;
        if (status is null && firing.TicketId is not null)
        {
            var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            if (ticket is null) return false;
            status = ticket.Status;
        }
        return ConditionEvaluators.TicketInColumn(c, status);
    }

    private async Task<bool> EvaluateMinDescriptionLengthAsync(ProjectRuntime rt, MinDescriptionLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        return ticket is not null && ConditionEvaluators.MinDescriptionLength(c, ticket.Description);
    }

    private async Task<bool> EvaluateFieldLengthAsync(ProjectRuntime rt, FieldLengthConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.FieldLength(c, ticket.Title, ticket.Description);
    }

    private async Task<bool> EvaluatePriorityAsync(ProjectRuntime rt, PriorityConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.Priority(c, ticket.Priority);
    }

    private async Task<bool> EvaluateLabelsAsync(ProjectRuntime rt, LabelsConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.Labels(c, ticket.Labels.Select(l => l.Name).ToList());
    }

    private async Task<bool> EvaluateAssignedToAsync(ProjectRuntime rt, AssignedToConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.AssignedTo(c, ticket.AssignedTo);
    }

    private async Task<bool> EvaluateHasParentAsync(ProjectRuntime rt, HasParentConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.HasParent(c, ticket.ParentId);
    }

    private async Task<bool> EvaluateAllSubTicketsInStatusAsync(ProjectRuntime rt, AllSubTicketsInStatusConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return false;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.AllSubTicketsInStatus(c, ticket.SubTickets);
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
        return ConditionEvaluators.CompareCount(c.Operator, count, c.Value);
    }

    private async Task<bool> EvaluateTicketAgeAsync(ProjectRuntime rt, TicketAgeConditionSpec c, TriggerFiring firing)
    {
        if (firing.TicketId is null) return true;
        var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
        if (ticket is null) return false;
        return ConditionEvaluators.TicketAge(c, ticket.CreatedAt, ticket.UpdatedAt, DateTime.UtcNow);
    }

    // ── Action execution ────────────────────────────────────────────────────

    internal sealed class ActionState
    {
        public AgentRun? LastRun;
        public string? StatusBeforeMove;
        public string? StatusAfterMove;
        public string? AssigneeBeforeMove;
    }

    public async Task<AgentRun?> ExecuteAutomationAsync(
        ProjectRuntime rt,
        Automation automation,
        TriggerFiring firing,
        CancellationToken ct,
        ITrigger? trigger = null,
        TriggerContext? tctx = null)
    {
        string? chainKey = null;
        if (firing.TicketId is int tid)
            chainKey = $"{automation.Id}:{tid}";
        if (chainKey is not null && !_inFlightChains.TryAdd(chainKey, 0))
        {
            _logger.LogDebug("Chain {Key} already in flight — skipping", chainKey);
            return null;
        }

        // statusChange delivery is exactly-once: persist consumption before any action can
        // detach, fail, or be interrupted by an engine restart. TriggerHandler performs the
        // duplicate gate; this call also protects direct executor callers and tests.
        if (trigger is StatusChangeTrigger statusTrigger && tctx is not null)
            statusTrigger.TryConsumeFiring(tctx, firing);

        var state = new ActionState();
        bool committed = false;
        bool runAgentDispatched = false;
        bool detached = false;

        async Task CommitAsync(DateTime? completedAt = null)
        {
            if (committed || trigger is null || tctx is null) return;
            committed = true;
            try { await trigger.CommitFiringAsync(tctx, firing, completedAt); }
            catch (Exception ex) { _logger.LogWarning(ex, "CommitFiring failed for {Id}", automation.Id); }
        }

        // The engine tick awaits ExecuteAutomationAsync, so nothing here may block for long:
        // one long action would freeze trigger evaluation for every project. Fast actions run
        // inline; at the first long-running action (a consolidation subprocess, a PowerShell
        // script) the remaining chain is detached to a background task, guarded against
        // overlapping executions of the same (automation, ticket).
        async Task<AgentRun?> ExecuteFromAsync(int startIndex, bool background)
        {
            for (int i = startIndex; i < automation.Actions.Count; i++)
            {
                var action = automation.Actions[i];

                if (!background && action is ConsolidateAgentMemoryActionSpec or ExecutePowerShellActionSpec or HttpRequestActionSpec)
                {
                    var coalesceGlobally = IsTicketIndependentDetachedAction(action);
                    var guardKey = coalesceGlobally
                        ? $"{rt.Slug}:{automation.Id}:global-detached"
                        : chainKey ?? $"{rt.Slug}:{automation.Id}:detached";
                    var ownsDetachedGuard = coalesceGlobally || chainKey is null;
                    if (ownsDetachedGuard && !_inFlightDetachedActions.TryAdd(guardKey, 0))
                    {
                        _logger.LogInformation(
                            "Coalesced overlapping ticket-independent actions for {Project}/{Id}",
                            rt.Slug, automation.Id);
                        return state.LastRun;
                    }
                    detached = true;
                    var idx = i;
                    _ = Task.Run(async () =>
                    {
                        try { await ExecuteFromAsync(idx, background: true); }
                        catch (OperationCanceledException) { /* engine shutdown */ }
                        catch (Exception ex) { _logger.LogWarning(ex, "Detached automation actions failed for {Id}", automation.Id); }
                        finally
                        {
                            if (ownsDetachedGuard)
                                _inFlightDetachedActions.TryRemove(guardKey, out _);
                            // Mirrors the outer finally: a dispatched runAgent hands chain
                            // ownership to HandleRunCompletionAsync.
                            if (!runAgentDispatched && chainKey is not null)
                                _inFlightChains.TryRemove(chainKey, out _);
                        }
                    }, CancellationToken.None);
                    return state.LastRun;
                }

                switch (action)
                {
                    case RunAgentActionSpec a:
                    {
                        var remaining = automation.Actions.Skip(i + 1).ToList();
                        var skip = await ExecuteRunAgentActionAsync(rt, automation, firing, a, ct, CommitAsync, state, remaining, chainKey);
                        runAgentDispatched = !skip;
                        // Whether skipped or dispatched, remaining actions are NOT processed here.
                        if (skip) return null;
                        return state.LastRun;
                    }
                    default:
                        // Everything except runAgent goes through the single shared dispatch —
                        // see ExecuteChainActionAsync. An unregistered type throws here (the
                        // pre-run chain is allowed to fail loudly).
                        if (await ExecuteChainActionAsync(rt, firing, action, state, parentRun: null, ct))
                            return state.LastRun;
                        break;
                }
            }
            await CommitAsync(DateTime.UtcNow);
            return state.LastRun;
        }

        try
        {
            return await ExecuteFromAsync(0, background: false);
        }
        finally
        {
            if (chainKey is not null && !runAgentDispatched && !detached)
                _inFlightChains.TryRemove(chainKey, out _);
        }
    }

    private static bool IsTicketIndependentDetachedAction(ActionSpec action)
    {
        if (action is ConsolidateAgentMemoryActionSpec) return true;
        return action is ExecutePowerShellActionSpec { CoalesceOverlapping: true };
    }

    /// <summary>
    /// Executes a complete chain and does not return while a detached action or runAgent continuation
    /// still owns its per-ticket chain slot. Queue consumers use this without blocking trigger polling.
    /// </summary>
    public async Task<AgentRun?> ExecuteAutomationToCompletionAsync(
        ProjectRuntime rt, Automation automation, TriggerFiring firing, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        if (firing.TicketId is not int ticketId)
        {
            await ExecuteAutomationAsync(rt, automation, firing, ct);
            return null;
        }
        var chainKey = $"{automation.Id}:{ticketId}";
        await ExecuteAutomationAsync(rt, automation, firing, ct);
        while (_inFlightChains.ContainsKey(chainKey))
            await Task.Delay(250, ct);
        return _runs.AllForTicket(rt.Slug, ticketId)
            .Where(r => r.StartedAt >= startedAt)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefault();
    }

    // Returns true when the caller should abort (gate not passed).
    // When false, the run has been DISPATCHED (not awaited).
    private async Task<bool> ExecuteRunAgentActionAsync(
        ProjectRuntime rt,
        Automation automation,
        TriggerFiring firing,
        RunAgentActionSpec a,
        CancellationToken ct,
        Func<DateTime?, Task> commitAsync,
        ActionState state,
        List<ActionSpec> remainingActions,
        string? chainKey)
    {
        var (skip, runTask, agentName) = await StartAgentRunAsync(rt, firing, a, ct);
        if (skip || runTask is null) return true;

        var statusBefore = state.StatusBeforeMove;
        var statusAfter = state.StatusAfterMove;
        var assigneeBefore = state.AssigneeBeforeMove;
        _ = HandleRunCompletionAsync(runTask, rt, firing, a, agentName, statusBefore, statusAfter, assigneeBefore, remainingActions, commitAsync, chainKey, ct);
        state.LastRun = null;
        return false;
    }

    // Resolves the agent name, applies the skip gate, and starts the run (without awaiting it).
    // Returns skip=true when the run must not proceed (placeholder unresolved or gate skip);
    // otherwise runTask is the in-flight run and agentName the resolved slug.
    private async Task<(bool skip, Task<AgentRun>? runTask, string agentName)> StartAgentRunAsync(
        ProjectRuntime rt,
        TriggerFiring firing,
        RunAgentActionSpec a,
        CancellationToken ct)
    {
        var agentName = a.Agent;
        if (agentName.Contains("{assignee}"))
        {
            if (firing.TicketId is null)
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but no ticketId in firing — skipping");
                return (true, null, agentName);
            }
            var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
            var assignee = t?.AssignedTo;
            if (string.IsNullOrEmpty(assignee))
            {
                _logger.LogWarning("Placeholder {{assignee}} in Agent but ticket #{Id} has no assignee — skipping", firing.TicketId);
                return (true, null, agentName);
            }
            agentName = agentName.Replace("{assignee}", assignee);
        }

        var skillFile = $"{agentName}/SKILL.md";
        var group = string.IsNullOrEmpty(a.ConcurrencyGroup)
            ? agentName
            : a.ConcurrencyGroup
                .Replace("{assignee}", agentName)
                .Replace("{ticketId}", firing.TicketId?.ToString() ?? "none");

        if (await _runState.ShouldSkipAsync(rt, a, firing, agentName, group)) return (true, null, agentName);

        var project = await _projects.GetProjectAsync(rt.Slug);
        var fallbackModel = project?.FallbackModel;

        var effectiveModel = a.Model;

        // Resolve model from member's DefaultModel if action model is null
        if (effectiveModel is null)
        {
            var member = await _members.GetMemberBySlugAsync(rt.Slug, agentName);
            var memberDefault = member?.DefaultModel ?? project?.LocalModelName;
            effectiveModel = string.IsNullOrWhiteSpace(memberDefault) ? null : memberDefault;
        }

        // Decide which CLI runs this dispatch (claude, claude+Ollama env, or grok).
        var routing = ModelRouting.Resolve(effectiveModel, project?.LocalModelBaseUrl);
        var target = routing.ToTarget(effectiveModel, a.Env);

        // The quota fallback can be any available model — resolve its own provider/env. An
        // unusable fallback (grok CLI missing, Ollama without base URL) disables the fallback
        // rather than failing the primary run. The fallback env starts from the raw action env
        // so the primary's routing extras (e.g. Ollama's ANTHROPIC_*) don't leak into it.
        var fallbackRouting = fallbackModel is null ? null : ModelRouting.Resolve(fallbackModel, project?.LocalModelBaseUrl);
        if (fallbackRouting?.Error is not null)
        {
            _logger.LogWarning("Fallback model '{Fallback}' is unusable ({Error}) — fallback disabled for this run",
                fallbackModel, fallbackRouting.Error);
            fallbackModel = null;
            fallbackRouting = null;
        }
        var fallbackTarget = fallbackModel is null || fallbackRouting is null
            ? null
            : fallbackRouting.ToTarget(fallbackModel, a.Env);

        var runCtx = new AgentRunContext
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
            LockTimeoutMinutes = a.LockTimeoutMinutes,
            Target = target,
            FallbackTarget = fallbackTarget,
            ExtraContext = a.Context,
            RetryOnResumeFailure = true,
            MaxRunDuration = TimeSpan.FromMinutes(30),
        };
        _sessions.SetLastDispatched(rt.Workspace!, agentName, DateTime.UtcNow);
        if (firing.TicketId is not null)
        {
            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get("ActAgentStarted", agentName), "automation"); }
            catch { /* non-blocking */ }
        }

        return (false, _runner.RunAsync(runCtx, ct), agentName);
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
        Func<DateTime?, Task> commitAsync,
        string? chainKey,
        CancellationToken ct)
    {
        try
        {

        AgentRun run;
        try { run = await runTask; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "runAgent {Agent} crashed for ticket #{Id}", agentName, firing.TicketId);
            return;
        }

        if (run.Status == AgentRunStatus.Completed)
            await commitAsync(DateTime.UtcNow);

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

        // Quota/spend failures: park in Blocked regardless of restoreStatusOnFail.
        // Otherwise assignee-dispatch (restore → Todo → re-fire every 30s) and assignee-resume
        // (leave InProgress → re-fire every 30s) both spin forever against a hard limit.
        if (run.HitQuota
            && run.Status is AgentRunStatus.Failed or AgentRunStatus.Stopped
            && firing.TicketId is not null)
        {
            try
            {
                var ticket = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (ticket is not null
                    && !string.Equals(ticket.Status, "Blocked", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ticket.Status, "Done", StringComparison.OrdinalIgnoreCase))
                {
                    await _tickets.MoveTicketAsync(rt.Slug, firing.TicketId.Value, "Blocked", "automation");
                    try
                    {
                        await _tickets.AddCommentAsync(rt.Slug, firing.TicketId.Value,
                            "Agent stopped: usage/quota limit (primary and fallback exhausted). " +
                            "Parked in Blocked to avoid a re-dispatch loop. " +
                            "Move back to Todo when quota recovers or after changing the project fallback model.",
                            "automation");
                    }
                    catch { /* comment is best-effort */ }
                    _logger.LogWarning(
                        "Parked #{Id} in Blocked after quota failure (agent {Agent})",
                        firing.TicketId, agentName);
                }
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning(
                    "Quota failure on #{Id} (agent {Agent}) but no Blocked column — leaving ticket in place",
                    firing.TicketId, agentName);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to park #{Id} after quota", firing.TicketId); }
        }
        else if (spec.RestoreStatusOnFail
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
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore ticket #{Id} status", firing.TicketId); }
        }

        await ProcessPostRunActionsAsync(rt, firing, run, remainingActions, commitAsync, ct);

        } // end try
        finally
        {
            if (chainKey is not null)
                _inFlightChains.TryRemove(chainKey, out _);
        }
    }

    // Runs the side-effect actions that follow a runAgent. A second runAgent in the chain
    // (e.g. the judge that decides whether to advance the ticket) is dispatched here, awaited,
    // and its own trailing actions are processed recursively. Without this, the chained judge
    // run would never fire and tickets would stall in their column.
    private async Task ProcessPostRunActionsAsync(
        ProjectRuntime rt,
        TriggerFiring firing,
        AgentRun precedingRun,
        List<ActionSpec> actions,
        Func<DateTime?, Task> commitAsync,
        CancellationToken ct)
    {
        for (int i = 0; i < actions.Count; i++)
        {
            var post = actions[i];
            try
            {
                switch (post)
                {
                    case RunAgentActionSpec ra:
                    {
                        var (skip, runTask, agentName) = await StartAgentRunAsync(rt, firing, ra, ct);
                        if (skip || runTask is null) return;

                        AgentRun chainedRun;
                        try { chainedRun = await runTask; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "chained runAgent {Agent} crashed for ticket #{Id}", agentName, firing.TicketId);
                            return;
                        }

                        if (chainedRun.Status == AgentRunStatus.Completed)
                            await commitAsync(DateTime.UtcNow);

                        if (firing.TicketId is not null)
                        {
                            var statusKey = chainedRun.Status switch
                            {
                                AgentRunStatus.Completed => "ActAgentCompleted",
                                AgentRunStatus.Failed    => "ActAgentFailed",
                                AgentRunStatus.Stopped   => "ActAgentStopped",
                                _                        => "ActAgentCompleted",
                            };
                            try { await _tickets.AddActivityAsync(rt.Slug, firing.TicketId.Value, _loc.Get(statusKey, agentName), "automation"); }
                            catch { /* non-blocking */ }
                        }

                        var rest = actions.Skip(i + 1).ToList();
                        await ProcessPostRunActionsAsync(rt, firing, chainedRun, rest, commitAsync, ct);
                        return;
                    }
                    default:
                        // Single shared dispatch (fix for the silent createTicket/
                        // moveTicketStatus drop, ticket §2.1): every non-runAgent action
                        // runs through ExecuteChainActionAsync. An unregistered type throws
                        // there and surfaces as the warning below instead of vanishing —
                        // the post-run chain keeps going, per-action try/catch semantics.
                        if (await ExecuteChainActionAsync(rt, firing, post, new ActionState(), precedingRun, ct))
                            return;
                        break;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Post-run action {Type} failed", post.GetType().Name); }
        }
    }

    // ── Single action dispatch ──────────────────────────────────────────────
    //
    // The ONE place where chain actions are routed to their executors, shared by the
    // pre-run path (ExecuteAutomationAsync) and the post-run path
    // (ProcessPostRunActionsAsync). Adding a new action type means adding exactly one
    // case here — ActionDispatchTests enumerates every ActionSpec subclass by
    // reflection and fails if one is missing. runAgent is the only exception: each
    // path owns its dispatch/hand-off semantics.
    //
    // Returns true when the chain must abort (a gate action said stop).
    private async Task<bool> ExecuteChainActionAsync(
        ProjectRuntime rt,
        TriggerFiring firing,
        ActionSpec action,
        ActionState state,
        AgentRun? parentRun,
        CancellationToken ct)
    {
        switch (action)
        {
            case MoveTicketStatusActionSpec m when firing.TicketId is not null:
                await _ticketMutation.ExecuteMoveTicketStatusAsync(rt, firing, m, state);
                return false;
            case SetLabelsActionSpec s when firing.TicketId is not null:
                await _ticketMutation.ExecuteSetLabelsAsync(rt, firing, s);
                return false;
            case AddCommentActionSpec ac when firing.TicketId is not null:
                await _ticketMutation.ExecuteAddCommentAsync(rt, firing, ac);
                return false;
            case AssignTicketActionSpec at when firing.TicketId is not null:
                await _ticketMutation.ExecuteAssignTicketAsync(rt, firing, at);
                return false;
            case MoveTicketStatusActionSpec or SetLabelsActionSpec or AddCommentActionSpec or AssignTicketActionSpec:
                // Ticket-scoped action on a firing with no ticket (e.g. interval trigger):
                // skipping is the only sane option, but never a silent one.
                _logger.LogWarning("{Type} skipped: the trigger firing carries no ticket", action.GetType().Name);
                return false;
            case CommitAgentMemoryActionSpec cm:
                await _agentMemory.ExecuteCommitAgentMemoryAsync(rt, cm, firing);
                return false;
            case ConsolidateAgentMemoryActionSpec csm:
                await _agentMemory.ExecuteConsolidateAgentMemoryAsync(rt, csm, firing, parentRun, ct);
                return false;
            case CreateTicketActionSpec cta:
                await _ticketMutation.ExecuteCreateTicketAsync(rt, cta);
                return false;
            case HttpRequestActionSpec hr:
                return await _network.ExecuteHttpRequestAsync(hr, rt, firing, ct);
            case ExecutePowerShellActionSpec ps:
                return await _network.ExecutePowerShellAsync(ps, rt.Workspace!, rt.Slug, firing, ct);
            case RunAgentActionSpec:
                throw new InvalidOperationException(
                    "runAgent is dispatched by the chain owners, never by ExecuteChainActionAsync.");
            default:
                throw new NotSupportedException(
                    $"Unhandled action type {action.GetType().Name}. Register it in ActionExecutor.ExecuteChainActionAsync.");
        }
    }

    // ── Static helpers (internal — tested directly) ─────────────────────────

    internal static string? FirstConfiguredModel(params string?[] candidates) =>
        candidates.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model))?.Trim();

    /// <summary>Expands date/time placeholders in createTicket title/description fields.</summary>
    internal static string ResolveCreateTicketPlaceholders(string s, DateTime now)
    {
        var today = now.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var firstOfMonth = new DateTime(today.Year, today.Month, 1);
        return s
            .Replace("{now}", now.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{date}", today.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HH:mm"))
            .Replace("{monday}", monday.ToString("yyyy-MM-dd"))
            .Replace("{firstOfMonth}", firstOfMonth.ToString("yyyy-MM-dd"));
    }

    // ── Dependency gate helpers ─────────────────────────────────────────────

    private const string BlockerMarkerPrefix = "<!-- dep-blocked:";

    // Posts a comment naming the unresolved blockers, but only when no existing automation
    // comment already covers exactly this set (avoids re-posting every dispatch cycle).
    private async Task PostBlockerCommentIfNewAsync(
        string projectSlug,
        int ticketId,
        List<Models.TicketDependencyInfo> unresolved,
        List<Models.Comment> existingComments)
    {
        var ids = string.Join(",", unresolved.OrderBy(b => b.TicketId).Select(b => b.TicketId));
        var marker = $"{BlockerMarkerPrefix} {ids} -->";
        if (existingComments.Any(c => c.Content.Contains(marker, StringComparison.Ordinal)))
            return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Dispatch blocked: this ticket has unresolved blockers:");
        foreach (var b in unresolved)
            sb.AppendLine($"- #{b.TicketId} **{b.Title}** (status: {b.Status})");
        sb.AppendLine();
        sb.AppendLine("Dispatch will resume automatically once all blockers reach Done.");
        sb.Append(marker);
        try
        {
            await _tickets.AddCommentAsync(projectSlug, ticketId, sb.ToString(), "automation");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post blocker comment for ticket #{Id}", ticketId);
        }
    }
}
