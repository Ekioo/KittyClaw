using System.Collections.Concurrent;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Event-driven workflow engine. It claims at most one ticket per active column.
/// </summary>
public sealed class ColumnProcessingEngine : BackgroundService
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? LastTickAt { get; private set; }

    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnExecutionService _executions;
    private readonly IColumnAgentDispatcher _dispatcher;
    private readonly ColumnActionExecutor _actions;
    private readonly ColumnMemoryCapitalizationService _memory;
    private readonly TicketWorktreeService? _worktrees;
    private readonly WorktreeFinalizationCoordinator? _finalization;
    private readonly WorktreeMergeQueueService? _mergeQueue;
    private readonly ILogger<ColumnProcessingEngine> _logger;
    private readonly StartupWorkGate? _startupGate;
    private readonly AgentRunRegistry? _runs;
    private readonly JevQualificationService? _jev;
    private readonly JevConfigurationService? _jevConfiguration;
    private readonly ConcurrentDictionary<string, byte> _pendingProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _activeProcessors = new();
    private readonly SemaphoreSlim _wake = new(0);
    internal Func<Task>? BeforeFinalSuccessValidationAsync { get; set; }
    internal TimeSpan PeriodicSweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    public ColumnProcessingEngine(
        ProjectService projects, TicketService tickets, ColumnProcessorService processors,
        ColumnExecutionService executions, IColumnAgentDispatcher dispatcher, ColumnActionExecutor actions,
        ColumnMemoryCapitalizationService memory,
        ILogger<ColumnProcessingEngine> logger,
        TicketWorktreeService? worktrees = null,
        WorktreeFinalizationCoordinator? finalization = null,
        WorktreeMergeQueueService? mergeQueue = null,
        StartupWorkGate? startupGate = null,
        AgentRunRegistry? runs = null,
        JevQualificationService? jev = null, JevConfigurationService? jevConfiguration = null)
    {
        _projects = projects;
        _tickets = tickets;
        _processors = processors;
        _executions = executions;
        _dispatcher = dispatcher;
        _actions = actions;
        _memory = memory;
        _logger = logger;
        _worktrees = worktrees;
        _finalization = finalization;
        _mergeQueue = mergeQueue;
        _startupGate = startupGate;
        _runs = runs;
        _jev = jev;
        _jevConfiguration = jevConfiguration;
        _tickets.TicketStatusChanged += OnTicketChanged;
        _tickets.TicketCreated += OnTicketCreated;
        _tickets.TicketCommentAdded += OnTicketCommentAdded;
    }

    private void OnTicketChanged(string slug, int _, string __, string ___) => Signal(slug);
    private void OnTicketCreated(string slug, int _) => Signal(slug);
    private void OnTicketCommentAdded(string slug, int ticketId, int commentId, string author, string _) => Signal(slug);

    public void Signal(string projectSlug)
    {
        _pendingProjects[projectSlug] = 0;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupGate is not null)
            await _startupGate.WaitAsync(stoppingToken);
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            await _executions.RecoverInterruptedAsync(project.Slug);
            if (!project.IsPaused) Signal(project.Slug);
        }

        var nextPeriodicSweepAt = DateTime.UtcNow.Add(PeriodicSweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var untilSweep = nextPeriodicSweepAt - now;
                await _wake.WaitAsync(untilSweep > TimeSpan.Zero ? untilSweep : TimeSpan.Zero, stoppingToken);
                now = DateTime.UtcNow;
                // A real clock-based sweep prevents a busy project from keeping the pending
                // dictionary non-empty and starving due retries in otherwise quiet projects.
                if (now >= nextPeriodicSweepAt)
                {
                    foreach (var project in (await _projects.ListProjectsAsync()).Where(p => !p.IsPaused))
                        _pendingProjects[project.Slug] = 0;
                    nextPeriodicSweepAt = now.Add(PeriodicSweepInterval);
                }

                foreach (var slug in _pendingProjects.Keys)
                {
                    _pendingProjects.TryRemove(slug, out _);
                    await ScheduleProjectAsync(slug, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Column processing engine loop failed"); }
            LastTickAt = DateTime.UtcNow;
        }
    }

    private async Task ScheduleProjectAsync(string slug, CancellationToken stoppingToken)
    {
        var project = await _projects.GetProjectAsync(slug);
        if (project is null || project.IsPaused || _runs?.IsUpdating == true) return;
        foreach (var processor in await _processors.ListEnabledAsync(slug))
        {
            var key = $"{slug}:{processor.Id}";
            if (_activeProcessors.TryGetValue(key, out var active) && !active.IsCompleted) continue;
            var execution = await _executions.ClaimNextAsync(slug, processor, DateTime.UtcNow);
            if (execution is null) continue;
            var task = ProcessAsync(slug, processor, execution, stoppingToken);
            _activeProcessors[key] = task;
            _ = task.ContinueWith(completedTask =>
            {
                _activeProcessors.TryRemove(key, out var _);
                Signal(slug);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal async Task ProcessAsync(
        string slug, ColumnProcessor processor, ColumnExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.JevClassicFallback) processor = processor.WithoutJev();
        if (processor.JevObservationOnly || processor.JevAcceptanceValidation) return;
        using var updateLease = _runs?.TrackExecution();
        IDisposable? finalizationLease = null;
        int? rootTicketId = null;
        try
        {
            // Apply only this execution's approved snapshot, leaving the shared column definition intact.
            if (execution.JevExecutionProfileJson is { } profileJson)
            {
                var profile = System.Text.Json.JsonSerializer.Deserialize<JevExecutionProfile>(profileJson)!;
                var allowed = processor.JevExecutionProfiles.FirstOrDefault(p => JevWorkReviewService.Hash(p) == JevWorkReviewService.Hash(profile));
                if (allowed is null)
                {
                    await _executions.FailAttemptAsync(slug, execution, processor, "The approved JEV execution profile is no longer authorized.", processor.Name);
                    return;
                }
                processor = System.Text.Json.JsonSerializer.Deserialize<ColumnProcessor>(System.Text.Json.JsonSerializer.Serialize(processor))!;
                processor.Model = profile.Model;
                processor.AvailableSkills = profile.Skills.Union(processor.RequiredSkills, StringComparer.OrdinalIgnoreCase).ToList();
                processor.RecommendedSkills = profile.Skills;
            }
            else if (processor.JevExecutionSelection)
            {
                await _executions.FailAttemptAsync(slug, execution, processor, "A manually approved JEV execution profile is required.", processor.Name);
                return;
            }
            if (_worktrees is not null && _finalization is not null)
            {
                rootTicketId = await _worktrees.ResolveRootTicketIdAsync(slug, execution.TicketId);
                finalizationLease = _finalization.Enter(slug, rootTicketId.Value);
            }
            // Activity authors are user-facing. Keep the stable column id in run names,
            // concurrency groups and logs, but attribute board history to the processor's
            // configured display name instead of leaking an implementation id such as column-11.
            var activityAuthor = processor.Name;
            var ticket = await _tickets.GetTicketAsync(slug, execution.TicketId);
            if (ticket is null)
            {
                await _executions.FailAttemptAsync(slug, execution, processor, "Le ticket n'existe plus.", activityAuthor);
                return;
            }

            // A host can stop after an external side effect succeeds but before its local
            // checkpoint is committed. Replaying that action could duplicate a webhook or
            // script effect, so an indeterminate in-flight action is routed/held instead.
            if (!string.IsNullOrWhiteSpace(execution.CurrentActionId))
            {
                var interrupted = processor.BeforeActions.Concat(processor.AfterActions)
                    .FirstOrDefault(action => string.Equals(
                        action.Id, execution.CurrentActionId, StringComparison.OrdinalIgnoreCase))
                    ?? new ColumnProcessorAction(
                        execution.CurrentActionId,
                        new SetLabelsActionSpec(),
                        processor.TechnicalFailureColumnId);
                await _executions.RouteActionFailureAsync(
                    slug, execution, processor, interrupted,
                    $"L’action '{execution.CurrentActionId}' a été interrompue ; son résultat externe est incertain.",
                    activityAuthor,
                    outcomeUncertain: true);
                return;
            }

            var context = new ExecutionContext(slug, processor, execution, ticket, cancellationToken,
                await HasSuccessRouteAsync(slug, processor));
            await RunStagesAsync(context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Column processor {ProcessorId} failed for ticket {TicketId}", processor.Id, execution.TicketId);
            await _executions.FailAttemptAsync(slug, execution, processor, ex.Message, processor.Name);
        }
        finally
        {
            finalizationLease?.Dispose();
            if (rootTicketId is not null && _mergeQueue is not null)
            {
                try { await _mergeQueue.RecoverTerminalWorktreesAsync(slug, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Unable to queue terminal worktree finalization for ticket {TicketId}", execution.TicketId);
                }
            }
        }
    }

    internal Action<ColumnExecutionStage>? StageChanged { get; set; }

    private sealed class ExecutionContext(string slug, ColumnProcessor processor, ColumnExecution execution,
        Ticket ticket, CancellationToken cancellationToken, bool hasSuccessRoute)
    {
        public string Slug { get; } = slug;
        public ColumnProcessor Processor { get; set; } = processor;
        public ColumnExecution Execution { get; } = execution;
        public Ticket Ticket { get; set; } = ticket;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public bool HasSuccessRoute { get; } = hasSuccessRoute;
        public ColumnAgentResult Result { get; set; } = null!;
        public JevRoutingProposal? RoutingProposal { get; set; }
        public int? JevTarget => RoutingProposal is null ? null : int.Parse(RoutingProposal.Choice![7..]);
        public bool TransitionBeforeActions { get; set; }
    }

    private async Task RunStagesAsync(ExecutionContext context)
    {
        var stage = ColumnExecutionStage.Preparation;
        while (stage != ColumnExecutionStage.Stopped)
        {
            if ((await _executions.GetAsync(context.Slug, context.Execution.Id))?.Status is not
                (ColumnExecutionStatus.Running or ColumnExecutionStatus.Completed))
                return;
            StageChanged?.Invoke(stage);
            var proceed = stage switch
            {
                ColumnExecutionStage.Preparation => context.HasSuccessRoute || await RunBeforeActionsAsync(context),
                ColumnExecutionStage.Agent => await RunAgentAsync(context),
                ColumnExecutionStage.Result => await RecordResultAsync(context),
                ColumnExecutionStage.RoutingAnalysis => await AnalyzeRoutingAsync(context),
                ColumnExecutionStage.RoutingValidation => await ValidateRoutingAsync(context),
                ColumnExecutionStage.DeferredPreparation => !context.HasSuccessRoute || await RunBeforeActionsAsync(context),
                ColumnExecutionStage.Finalization => await RunFinalActionsAsync(context),
                ColumnExecutionStage.Transition => await CommitTransitionAsync(context),
                _ => throw new ArgumentOutOfRangeException(nameof(stage))
            };
            stage = ColumnExecutionFlow.Next(stage, context.TransitionBeforeActions, proceed);
        }
    }

    private Task<bool> RunBeforeActionsAsync(ExecutionContext context) => ExecuteActionsAsync(
        context.Slug, context.Processor, context.Execution, context.Ticket,
        context.Processor.BeforeActions, null, context.Processor.Name, context.CancellationToken);

    private async Task<bool> RunAgentAsync(ExecutionContext context)
    {
        if (context.Execution.AgentCompleted)
        {
            context.Result = context.Execution.AgentResult
                ?? throw new InvalidOperationException("The agent checkpoint contains no result.");
            return true;
        }
        await _executions.SetRunIdAsync(context.Slug, context.Execution.Id, context.Execution.Id);
        var dispatch = await _dispatcher.DispatchAsync(context.Slug, context.Processor, context.Execution,
            context.Ticket, context.CancellationToken);
        // A durable question suspends routing, including malformed/missing final agent output.
        // An answer may already have queued a retry while the subprocess was exiting.
        if ((await _executions.GetAsync(context.Slug, context.Execution.Id))?.Status != ColumnExecutionStatus.Running)
            return false;
        if (dispatch.Result is null)
        {
            // An update/shutdown leaves the durable claim for startup recovery.
            if (_runs?.IsUpdating == true || context.CancellationToken.IsCancellationRequested) return false;
            await _executions.FailAttemptAsync(context.Slug, context.Execution, context.Processor,
                dispatch.Error ?? "Unknown processor failure.", context.Processor.Name);
            return false;
        }
        context.Result = dispatch.Result;
        var missing = context.Processor.RequiredSkills.Except(context.Result.SkillsUsed, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0) return true;
        await _executions.FailAttemptAsync(context.Slug, context.Execution, context.Processor,
            $"Required skills were not executed: {string.Join(", ", missing)}.", context.Processor.Name);
        return false;
    }

    private async Task<bool> RecordResultAsync(ExecutionContext context)
    {
        if (!context.Execution.AgentCompleted)
            await _executions.SaveAgentResultAsync(context.Slug, context.Execution, context.Result, context.Processor);
        if ((await _executions.GetAsync(context.Slug, context.Execution.Id))?.Status != ColumnExecutionStatus.Running)
            return false;
        await CapitalizeAsync(context.Slug, context.Processor, context.Execution, context.Result, context.CancellationToken);
        return true;
    }

    private async Task<bool> AnalyzeRoutingAsync(ExecutionContext context)
    {
        if (await _executions.StopSupersededAsync(context.Slug, context.Execution, context.Processor)) return false;
        var processor = context.Processor;
        if (!processor.UsesAutomaticJevRouting || _jev is null || _jevConfiguration is null ||
            string.Equals(context.Result.Outcome, "scheduled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.Result.Outcome, "wait_for_children", StringComparison.OrdinalIgnoreCase) ||
            !(await _jevConfiguration.GetAsync(context.Slug, context.CancellationToken)).Available) return true;
        context.Ticket = await _tickets.GetTicketAsync(context.Slug, context.Execution.TicketId)
            ?? throw new InvalidOperationException("The ticket no longer exists.");
        await using var db = _projects.GetProjectDb(context.Slug);
        var columns = await db.BoardColumns.AsNoTracking().ToListAsync(context.CancellationToken);
        await _jev.ObserveAsync(context.Slug, processor, context.Ticket, columns, context.CancellationToken, routingResult: context.Result);
        context.RoutingProposal = await _jev.GetAsync(context.Slug, context.Ticket.Id, context.CancellationToken);
        if (JevCertainty.Routing(context.RoutingProposal, processor.JevConfidenceThreshold)) return true;
        context.RoutingProposal = null;
        await _executions.RecordClassicFallbackAsync(context.Slug, context.Execution);
        context.Processor = processor.WithoutJev();
        return true;
    }

    private async Task<bool> ValidateRoutingAsync(ExecutionContext context)
    {
        var rejection = await _executions.ValidateSuccessContextAsync(context.Slug, context.Execution,
            context.Processor, context.Result, context.JevTarget);
        if (rejection is not null)
        {
            await _executions.FailAttemptAsync(context.Slug, context.Execution, context.Processor, rejection, context.Processor.Name);
            return false;
        }
        context.Ticket = await _tickets.GetTicketAsync(context.Slug, context.Execution.TicketId)
            ?? throw new InvalidOperationException("The ticket no longer exists.");
        context.TransitionBeforeActions = context.RoutingProposal is not null || context.HasSuccessRoute &&
            await IsSuccessOutcomeAsync(context.Slug, context.Processor, context.Result, context.JevTarget);
        return true;
    }

    private Task<bool> RunFinalActionsAsync(ExecutionContext context) => ExecuteActionsAsync(
        context.Slug, context.Processor, context.Execution, context.Ticket,
        context.TransitionBeforeActions
            ? context.Processor.BeforeActions.Concat(context.Processor.AfterActions).ToList()
            : context.Processor.AfterActions,
        context.Result, context.Processor.Name, context.CancellationToken);

    private async Task<bool> CommitTransitionAsync(ExecutionContext context)
    {
        // Success and JEV transitions must win atomically before external effects.
        if (context.TransitionBeforeActions && BeforeFinalSuccessValidationAsync is not null)
            await BeforeFinalSuccessValidationAsync();
        await _executions.CompleteAsync(context.Slug, context.Execution, context.Processor,
            context.Result, context.Processor.Name, context.RoutingProposal);
        if (!context.TransitionBeforeActions) return true;
        var completed = await _executions.GetAsync(context.Slug, context.Execution.Id);
        return completed?.Status == ColumnExecutionStatus.Completed;
    }

    private readonly SemaphoreSlim _memoryRetryGate = new(1, 1);

    /// <summary>Retries knowledge persistence only; never dispatches or routes a ticket.</summary>
    public async Task<bool> RetryMemoryAsync(string slug, string executionId, CancellationToken ct = default)
    {
        await _memoryRetryGate.WaitAsync(ct);
        try
        {
            var execution = await _executions.GetAsync(slug, executionId);
            if (execution?.AgentResult is not { } result
                || execution.Status is ColumnExecutionStatus.Running or ColumnExecutionStatus.Retrying or ColumnExecutionStatus.WaitingForChildren or ColumnExecutionStatus.WaitingForAnswer
                || execution.CapitalizationStatus is MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange)
                return false;
            var columnId = await _executions.FindProcessorColumnIdAsync(slug, execution.ProcessorId);
            var processor = columnId is int id ? await _processors.GetAsync(slug, id) : null;
            if (processor is null) return false;
            using var updateLease = _runs?.TrackExecution();
            var rootTicketId = _worktrees is not null
                ? await _worktrees.ResolveRootTicketIdAsync(slug, execution.TicketId) : execution.TicketId;
            using var finalizationLease = _finalization?.Enter(slug, rootTicketId);
            await CapitalizeAsync(slug, processor, execution, result, ct);
            return true;
        }
        finally { _memoryRetryGate.Release(); }
    }

    private async Task CapitalizeAsync(string slug, ColumnProcessor processor, ColumnExecution execution,
        ColumnAgentResult result, CancellationToken cancellationToken)
    {
        try
        {
            if (execution.CapitalizationStatus is not (MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange))
            {
                var capitalization = await _memory.CapitalizeAsync(
                    slug, processor.ColumnId, execution.Id, result.Lessons, cancellationToken, execution.TicketId);
                if (capitalization.Status == MemoryCapitalizationStatus.Failed)
                {
                    await _executions.SetCapitalizationAsync(slug, execution,
                        MemoryCapitalizationStatus.RetryRequired, capitalization.Error);
                    return;
                }

                // A validation rejection teaches the processor that most recently routed the
                // ticket into this column. The current validation processor still owns its own lessons.
                if (string.Equals(result.Outcome, "changes_requested", StringComparison.OrdinalIgnoreCase))
                {
                    var upstream = await _executions.FindUpstreamExecutionAsync(
                        slug, execution.TicketId, processor.ColumnId, execution.Id);
                    if (upstream is not null)
                    {
                        var upstreamColumnId = await _executions.FindProcessorColumnIdAsync(
                            slug, upstream.ProcessorId);
                        if (upstreamColumnId is int attributedColumnId)
                        {
                            var feedback = result.Lessons is { Count: > 0 }
                                ? result.Lessons
                                : string.IsNullOrWhiteSpace(result.Summary) ? [] : [result.Summary];
                            var attributed = await _memory.CapitalizeAsync(slug, attributedColumnId,
                                $"{execution.Id}-feedback-{upstream.Id}", feedback, cancellationToken, execution.TicketId);
                            if (attributed.Status == MemoryCapitalizationStatus.Failed)
                            {
                                await _executions.SetCapitalizationAsync(slug, execution,
                                    MemoryCapitalizationStatus.RetryRequired, attributed.Error);
                                return;
                            }
                        }
                    }
                }
                await _executions.SetCapitalizationAsync(slug, execution, capitalization.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await _executions.SetCapitalizationAsync(slug, execution, MemoryCapitalizationStatus.RetryRequired, ex.Message);
            _logger.LogWarning(ex, "Memory persistence needs recovery for execution {ExecutionId}", execution.Id);
        }
    }

    private async Task<bool> HasSuccessRouteAsync(string slug, ColumnProcessor processor)
    {
        await using var db = _projects.GetProjectDb(slug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        var targetIds = processor.Routes.Select(route => (int?)route.TargetColumnId)
            .Append(processor.DefaultTargetColumnId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        return await db.BoardColumns.AnyAsync(column =>
            targetIds.Contains(column.Id) && column.Role == ColumnRole.Success);
    }

    private async Task<bool> IsSuccessOutcomeAsync(
        string slug, ColumnProcessor processor, ColumnAgentResult result, int? jevTarget = null)
    {
        var targetId = jevTarget ?? processor.Routes.FirstOrDefault(route =>
            string.Equals(route.Outcome, result.Outcome, StringComparison.OrdinalIgnoreCase))?.TargetColumnId
            ?? processor.DefaultTargetColumnId;
        if (targetId is null) return false;
        await using var db = _projects.GetProjectDb(slug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        return await db.BoardColumns.AnyAsync(column =>
            column.Id == targetId.Value && column.Role == ColumnRole.Success);
    }

    private async Task<bool> ExecuteActionsAsync(
        string slug,
        ColumnProcessor processor,
        ColumnExecution execution,
        Ticket ticket,
        IReadOnlyList<ColumnProcessorAction> actions,
        ColumnAgentResult? agentResult,
        string activityAuthor,
        CancellationToken cancellationToken)
    {
        if (await _executions.StopSupersededAsync(slug, execution, processor)) return false;
        var completed = execution.CompletedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (completed.Contains(action.Id)) continue;
            if (await _executions.StopSupersededAsync(slug, execution, processor)) return false;
            await _executions.BeginActionAsync(slug, execution, action.Id);
            var result = await _actions.ExecuteAsync(
                slug, processor, execution, ticket, action, agentResult, cancellationToken);
            if (!result.Succeeded)
            {
                await _executions.ClearCurrentActionAsync(slug, execution);
                var error = $"Action '{action.Id}' ({action.Action.UiTypeKey}) en échec : {result.Error}";
                if (action.FailureTargetColumnId is not null)
                    await _executions.RouteActionFailureAsync(
                        slug, execution, processor, action, error, activityAuthor);
                else
                    await _executions.FailAttemptAsync(slug, execution, processor, error, activityAuthor);
                return false;
            }
            await _executions.CompleteActionAsync(slug, execution, action.Id);
            completed.Add(action.Id);
        }
        return true;
    }

    public override void Dispose()
    {
        _tickets.TicketStatusChanged -= OnTicketChanged;
        _tickets.TicketCreated -= OnTicketCreated;
        _tickets.TicketCommentAdded -= OnTicketCommentAdded;
        _wake.Dispose();
        base.Dispose();
    }
}
