using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Evaluates automation conditions and executes action sequences.
/// Owns the git semaphore and all Execute*ActionAsync helpers.
/// </summary>
internal sealed class ActionExecutor
{
    private readonly TicketService _tickets;
    private readonly MemberService _members;
    private readonly LabelService _labels;
    private readonly SessionRegistry _sessions;
    private readonly AgentRunRegistry _runs;
    private readonly AgentRunner _runner;
    private readonly CostTracker _cost;
    private readonly LocalizationService _loc;
    private readonly ProjectService _projects;
    private readonly RunStateManager _runState;
    private readonly ILogger _logger;

    // Serializes in-process git operations per repository. Keyed by the git cwd so one
    // repo's slow/hung git (bounded by ProcessRunner's timeout) can't stall other projects.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gitLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks in-flight action chains keyed by "{automationId}:{ticketId}".
    // Prevents concurrent chains for the same (automation, ticket) pair.
    private readonly ConcurrentDictionary<string, byte> _inFlightChains = new();

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
        _labels = labels;
        _sessions = sessions;
        _runs = runs;
        _runner = runner;
        _cost = cost;
        _loc = loc;
        _projects = projects;
        _runState = runState;
        _logger = logger;
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
            TicketInColumnConditionSpec c         => Task.FromResult(ConditionEvaluators.TicketInColumn(c, firing.TicketStatus)),
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

    private sealed class ActionState
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

                if (!background && action is ConsolidateAgentMemoryActionSpec or ExecutePowerShellActionSpec)
                {
                    var guardKey = chainKey ?? $"{automation.Id}:detached";
                    if (chainKey is null && !_inFlightChains.TryAdd(guardKey, 0))
                    {
                        _logger.LogDebug("Detached actions for {Id} already in flight — skipping", automation.Id);
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
                            // Mirrors the outer finally: a dispatched runAgent hands chain
                            // ownership to HandleRunCompletionAsync.
                            if (!runAgentDispatched) _inFlightChains.TryRemove(guardKey, out _);
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
                    case ConsolidateAgentMemoryActionSpec csm:
                        await ExecuteConsolidateAgentMemoryActionAsync(rt, csm, firing, parentRun: null, ct);
                        break;
                    case ExecutePowerShellActionSpec ps:
                    {
                        var abort = await ExecutePowerShellAsync(ps, rt.Workspace!, rt.Slug, firing, ct);
                        if (abort) return state.LastRun;
                        break;
                    }
                    case CreateTicketActionSpec cta:
                        await ExecuteCreateTicketActionAsync(rt, cta);
                        break;
                    default:
                        throw new NotSupportedException($"Unhandled action type {action.GetType().Name}. Register it in ActionExecutor.ExecuteAutomationAsync.");
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
        var effectiveEnv = a.Env;

        // Resolve model from member's DefaultModel if action model is null
        if (effectiveModel is null)
        {
            var member = await _members.GetMemberBySlugAsync(rt.Slug, agentName);
            var memberDefault = member?.DefaultModel ?? project?.LocalModelName;
            effectiveModel = string.IsNullOrWhiteSpace(memberDefault) ? null : memberDefault;
        }

        // Decide which CLI runs this dispatch (claude, claude+Ollama env, or grok).
        var routing = ModelRouting.Resolve(effectiveModel, project?.LocalModelBaseUrl);
        if (routing.ExtraEnv is not null)
        {
            var env = new Dictionary<string, string>(effectiveEnv);
            foreach (var kv in routing.ExtraEnv) env[kv.Key] = kv.Value;
            effectiveEnv = env;
        }

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
        Dictionary<string, string>? fallbackEnv = null;
        if (fallbackModel is not null)
        {
            fallbackEnv = new Dictionary<string, string>(a.Env);
            if (fallbackRouting?.ExtraEnv is not null)
                foreach (var kv in fallbackRouting.ExtraEnv) fallbackEnv[kv.Key] = kv.Value;
        }

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
            Env = effectiveEnv,
            Model = effectiveModel,
            Provider = routing.Provider,
            FallbackModel = fallbackModel,
            FallbackProvider = fallbackRouting?.Provider ?? CliProvider.Claude,
            FallbackEnv = fallbackEnv,
            ExtraContext = a.Context,
            RetryOnResumeFailure = true,
            ModelValidationError = routing.Error,
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
                    case CommitAgentMemoryActionSpec cm: await ExecuteCommitAgentMemoryActionAsync(rt, cm, firing); break;
                    case ConsolidateAgentMemoryActionSpec csm: await ExecuteConsolidateAgentMemoryActionAsync(rt, csm, firing, precedingRun, ct); break;
                    case AddCommentActionSpec ac when firing.TicketId is not null: await ExecuteAddCommentActionAsync(rt, firing, ac); break;
                    case SetLabelsActionSpec sl when firing.TicketId is not null: await ExecuteSetLabelsActionAsync(rt, firing, sl); break;
                    case AssignTicketActionSpec at when firing.TicketId is not null: await ExecuteAssignTicketActionAsync(rt, firing, at); break;
                    case ExecutePowerShellActionSpec ps: await ExecutePowerShellAsync(ps, rt.Workspace!, rt.Slug, firing, ct); break;
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
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Post-run action {Type} failed", post.GetType().Name); }
        }
    }

    private async Task ExecuteMoveTicketStatusActionAsync(ProjectRuntime rt, TriggerFiring firing, MoveTicketStatusActionSpec m, ActionState state)
    {
        if (string.Equals(firing.TicketStatus, m.To, StringComparison.OrdinalIgnoreCase))
            return;
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

    private async Task ExecuteConsolidateAgentMemoryActionAsync(
        ProjectRuntime rt,
        ConsolidateAgentMemoryActionSpec spec,
        TriggerFiring? firing,
        AgentRun? parentRun,
        CancellationToken ct)
    {
        try
        {
            var agent = spec.Agent;
            if (agent.Contains("{assignee}"))
            {
                if (firing?.TicketId is null)
                {
                    _logger.LogInformation("consolidateAgentMemory: {{assignee}} placeholder but no firing ticket — skipping");
                    return;
                }
                var t = await _tickets.GetTicketAsync(rt.Slug, firing.TicketId.Value);
                if (string.IsNullOrEmpty(t?.AssignedTo))
                {
                    _logger.LogInformation("consolidateAgentMemory: {{assignee}} placeholder but ticket #{Id} has no assignee — skipping", firing.TicketId);
                    return;
                }
                agent = agent.Replace("{assignee}", t.AssignedTo);
            }

            if (parentRun?.Status == AgentRunStatus.Failed && (parentRun.ExitCode ?? 0) < 0)
            {
                _logger.LogInformation("consolidateAgentMemory: parent run {Id} failed (exit {Exit}) — skipping", parentRun.RunId, parentRun.ExitCode);
                return;
            }

            var instructionPath = Path.Combine(
                rt.Workspace!,
                spec.InstructionFile.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(instructionPath))
            {
                _logger.LogWarning("consolidateAgentMemory: instruction file not found: {Path}", instructionPath);
                return;
            }

            var instructionContent = (await File.ReadAllTextAsync(instructionPath, ct))
                .Replace("{agentSlug}", agent);
            var eventsSummary = BuildEventsSummary(parentRun);

            const string scope = "consolidate";
            _sessions.Clear(rt.Workspace!, $"{scope}:{agent}", ticketId: null);

            var runCtx = new AgentRunContext
            {
                ProjectSlug = rt.Slug,
                WorkspacePath = rt.Workspace!,
                AgentName = agent,
                SkillFile = $"{agent}/SKILL.md",
                MaxTurns = spec.MaxTurns,
                ConcurrencyGroup = $"consolidate-{agent}",
                InlineSkillContent = instructionContent,
                ExtraContext = string.IsNullOrWhiteSpace(eventsSummary)
                    ? "No events were recorded for this run."
                    : eventsSummary,
                SessionScope = scope,
                Model = null,
                RetryOnResumeFailure = true,
                MaxRunDuration = TimeSpan.FromMinutes(30),
            };

            var run = await _runner.RunAsync(runCtx, ct);

            var memoryPaths = $"\".agents/{agent}/memory\" \".agents/{agent}/memory.md\"";
            var diff = await RunGitAsync(rt.Workspace!, $"diff --shortstat HEAD -- {memoryPaths}");
            var diffSummary = diff.stdout.Trim();
            _logger.LogInformation("consolidate {Agent}: run {Status} (exit {Exit}){Diff}",
                agent, run.Status, run.ExitCode,
                string.IsNullOrWhiteSpace(diffSummary) ? "" : $" — {diffSummary}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "consolidateAgentMemory: failed for {Agent}", spec.Agent);
        }
    }

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
            // Memory lives either in the new per-topic layout (.agents/{agent}/memory/) or, until an
            // agent has consolidated, in the legacy flat file (.agents/{agent}/memory.md). Commit
            // whichever exist — both, during the migration window.
            var memoryDirAbs = Path.Combine(workspace, ".agents", agent, "memory");
            var legacyAbs = Path.Combine(workspace, ".agents", agent, "memory.md");
            var hasDir = Directory.Exists(memoryDirAbs);
            var hasLegacy = File.Exists(legacyAbs);
            if (!hasDir && !hasLegacy)
            {
                _logger.LogInformation("commitAgentMemory: no memory found for {Agent} under {Path}", agent, Path.GetDirectoryName(legacyAbs));
                return;
            }

            // Prefer a nested .agents/.git repo if present (decouples agent config from main project repo).
            // Otherwise fall back to the main workspace repo.
            var agentsDir = Path.Combine(workspace, ".agents");
            string gitCwd;
            string relBase;
            if (Directory.Exists(Path.Combine(agentsDir, ".git")))
            {
                gitCwd = agentsDir;
                relBase = $"{agent}";
            }
            else if (Directory.Exists(Path.Combine(workspace, ".git")))
            {
                gitCwd = workspace;
                relBase = $".agents/{agent}";
            }
            else
            {
                _logger.LogDebug("commitAgentMemory: no git repo at {Path} or {Agents} — skipping", workspace, agentsDir);
                return;
            }

            // Pathspecs cover the new memory/ dir (recursively) and the legacy flat file. Git tolerates
            // pathspecs for paths that don't exist on disk (e.g. only the dir is present), so list both.
            var pathArgs = $"\"{relBase}/memory\" \"{relBase}/memory.md\"";

            var gitLock = _gitLocks.GetOrAdd(gitCwd, _ => new SemaphoreSlim(1, 1));
            await gitLock.WaitAsync();
            try
            {
                var diff = await RunGitAsync(gitCwd, $"diff --quiet --exit-code -- {pathArgs}");
                // diff --quiet returns 1 when there are tracked-file changes; untracked new topic
                // files are invisible to it, so also check `status --porcelain` before bailing.
                var status = await RunGitAsync(gitCwd, $"status --porcelain -- {pathArgs}");
                if (diff.exitCode == 0 && string.IsNullOrWhiteSpace(status.stdout))
                {
                    _logger.LogDebug("commitAgentMemory: {Agent} memory is clean, nothing to commit", agent);
                    return;
                }

                var add = await RunGitAsync(gitCwd, $"add -- {pathArgs}");
                if (add.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git add failed for {Agent}: {Err}", agent, add.stderr);
                    return;
                }

                var ticketSuffix = firing?.TicketId is int tid ? $" (#{tid})" : "";
                var msg = $"chore(memory): {agent}{ticketSuffix}";
                var commit = await RunGitAsync(gitCwd, $"commit --no-verify -m \"{msg}\" -- {pathArgs}");
                if (commit.exitCode != 0)
                {
                    _logger.LogWarning("commitAgentMemory: git commit failed for {Agent}: {Err}", agent, commit.stderr);
                    return;
                }

                _logger.LogInformation("commitAgentMemory: committed {Agent} memory", agent);
            }
            finally { gitLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "commitAgentMemory: failed to commit memory for {Agent}", cm.Agent);
        }
    }

    // Returns true when AbortOnFailure is set and the process exited with a non-zero code.
    private async Task<bool> ExecutePowerShellAsync(ExecutePowerShellActionSpec spec, string workspacePath, string slug, TriggerFiring firing, CancellationToken ct)
    {
        try
        {
            string Render(string s) => (s ?? string.Empty)
                .Replace("{ticketId}", firing.TicketId?.ToString() ?? "")
                .Replace("{ticketTitle}", firing.TicketTitle ?? "")
                .Replace("{slug}", slug ?? "");

            string scriptArg;
            if (!string.IsNullOrWhiteSpace(spec.ScriptFile))
            {
                var rendered = Render(spec.ScriptFile);
                var path = Path.IsPathRooted(rendered)
                    ? rendered
                    : Path.Combine(workspacePath, rendered);
                scriptArg = $"-File \"{path}\"";
            }
            else
            {
                var bytes = System.Text.Encoding.Unicode.GetBytes(Render(spec.Script));
                scriptArg = $"-EncodedCommand {Convert.ToBase64String(bytes)}";
            }

            var extraArgs = spec.Arguments.Count > 0
                ? " " + string.Join(" ", spec.Arguments.Select(a => $"\"{Render(a)}\""))
                : "";

            var pwshBin = ShellResolver.ResolvePowerShell();
            var res = await ProcessRunner.RunAsync(
                pwshBin,
                $"-NonInteractive -NoProfile {scriptArg}{extraArgs}",
                workspacePath,
                TimeSpan.FromSeconds(spec.TimeoutSeconds),
                spec.Env,
                ct);

            if (res.TimedOut)
            {
                _logger.LogWarning("executePowerShell timed out after {Timeout}s; process tree killed", spec.TimeoutSeconds);
                return spec.AbortOnFailure;
            }

            _logger.LogInformation("executePowerShell exited {Code}. stdout={Stdout} stderr={Stderr}",
                res.ExitCode, res.Stdout.Trim(), res.Stderr.Trim());

            if (res.ExitCode != 0)
            {
                _logger.LogWarning("executePowerShell non-zero exit ({Code}); abortOnFailure={Abort}", res.ExitCode, spec.AbortOnFailure);
                return spec.AbortOnFailure;
            }
        }
        catch (OperationCanceledException)
        {
            // Engine shutdown / chain cancellation — the process tree was already killed.
            _logger.LogWarning("executePowerShell cancelled");
            if (spec.AbortOnFailure) return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "executePowerShell failed");
            if (spec.AbortOnFailure) return true;
        }
        return false;
    }

    // ── Git helpers ─────────────────────────────────────────────────────────

    private static async Task<(int exitCode, string stdout, string stderr)> RunGitAsync(string cwd, string args)
    {
        // 2-minute cap: a git blocked on a credential prompt or a stale index.lock must not
        // hold the per-repo git lock forever.
        var res = await ProcessRunner.RunAsync("git", args, cwd, TimeSpan.FromMinutes(2));
        return (res.ExitCode ?? -1, res.Stdout, res.TimedOut ? "git timed out after 2 minutes" : res.Stderr);
    }

    private static string BuildEventsSummary(AgentRun? run)
    {
        if (run is null) return "";
        var lines = new List<string>();
        foreach (var ev in run.SnapshotBuffer())
        {
            if (ev.Kind is "assistant" or "tool_use" or "result")
            {
                var text = ev.Kind == "tool_use"
                    ? $"[tool_use] {ev.Text}: {TruncateDetail(ev.Detail, 120)}"
                    : $"[{ev.Kind}] {TruncateLine(ev.Text, 200)}";
                lines.Add(text);
            }
            if (lines.Count >= 80) break;
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    private static string TruncateLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string TruncateDetail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "{}";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
