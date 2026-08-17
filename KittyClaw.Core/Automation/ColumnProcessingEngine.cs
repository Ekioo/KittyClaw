using System.Collections.Concurrent;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KittyClaw.Core.Automation;

/// <summary>
/// Event-driven primary workflow engine. It claims at most one ticket per active column;
/// the legacy trigger engine remains registered independently for backward compatibility.
/// </summary>
public sealed class ColumnProcessingEngine : BackgroundService
{
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly ColumnProcessorService _processors;
    private readonly ColumnExecutionService _executions;
    private readonly IColumnAgentDispatcher _dispatcher;
    private readonly ColumnActionExecutor _actions;
    private readonly ColumnMemoryCapitalizationService _memory;
    private readonly ILogger<ColumnProcessingEngine> _logger;
    private readonly ConcurrentDictionary<string, byte> _pendingProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, int>> _ownerFeedbackSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _activeProcessors = new();
    private readonly SemaphoreSlim _wake = new(0);
    internal Func<Task>? BeforeFinalSuccessValidationAsync { get; set; }

    public ColumnProcessingEngine(
        ProjectService projects, TicketService tickets, ColumnProcessorService processors,
        ColumnExecutionService executions, IColumnAgentDispatcher dispatcher, ColumnActionExecutor actions,
        ColumnMemoryCapitalizationService memory,
        ILogger<ColumnProcessingEngine> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _processors = processors;
        _executions = executions;
        _dispatcher = dispatcher;
        _actions = actions;
        _memory = memory;
        _logger = logger;
        _tickets.TicketStatusChanged += OnTicketChanged;
        _tickets.TicketCreated += OnTicketCreated;
        _tickets.TicketCommentAdded += OnTicketCommentAdded;
    }

    private void OnTicketChanged(string slug, int _, string __, string ___) => Signal(slug);
    private void OnTicketCreated(string slug, int _) => Signal(slug);
    private void OnTicketCommentAdded(string slug, int ticketId, int commentId, string author, string _)
    {
        if (string.Equals(author, "owner", StringComparison.OrdinalIgnoreCase))
            _ownerFeedbackSignals.GetOrAdd(slug, _ => new())[ticketId] = commentId;
        Signal(slug);
    }

    public void Signal(string projectSlug)
    {
        _pendingProjects[projectSlug] = 0;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects)
        {
            await _executions.RecoverInterruptedAsync(project.Slug);
            if (!project.IsPaused) Signal(project.Slug);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
                // Periodic recovery also discovers processor edits and due retries.
                if (_pendingProjects.IsEmpty)
                    foreach (var project in (await _projects.ListProjectsAsync()).Where(p => !p.IsPaused))
                        _pendingProjects[project.Slug] = 0;

                foreach (var slug in _pendingProjects.Keys)
                {
                    _pendingProjects.TryRemove(slug, out _);
                    await ScheduleProjectAsync(slug, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Column processing engine loop failed"); }
        }
    }

    private async Task ScheduleProjectAsync(string slug, CancellationToken stoppingToken)
    {
        var project = await _projects.GetProjectAsync(slug);
        if (project is null || project.IsPaused) return;
        foreach (var processor in await _processors.ListEnabledAsync(slug))
        {
            var key = $"{slug}:{processor.Id}";
            if (_activeProcessors.TryGetValue(key, out var active) && !active.IsCompleted) continue;
            _ownerFeedbackSignals.TryGetValue(slug, out var feedbackSignals);
            var execution = await _executions.ClaimNextAsync(slug, processor, DateTime.UtcNow, feedbackSignals);
            if (execution is null) continue;
            feedbackSignals?.TryRemove(execution.TicketId, out _);
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
        try
        {
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

            var hasSuccessRoute = await HasSuccessRouteAsync(slug, processor);
            if (!hasSuccessRoute && !await ExecuteActionsAsync(
                    slug, processor, execution, ticket, processor.BeforeActions, null,
                    activityAuthor, cancellationToken))
                return;

            ColumnAgentResult result;
            if (execution.AgentCompleted)
            {
                result = execution.AgentResult
                    ?? throw new InvalidOperationException("Le checkpoint de l’agent ne contient aucun résultat.");
            }
            else
            {
                await _executions.SetRunIdAsync(slug, execution.Id, execution.Id);
                var dispatch = await _dispatcher.DispatchAsync(slug, processor, execution, ticket, cancellationToken);
                if (dispatch.Result is null)
                {
                    await _executions.FailAttemptAsync(slug, execution, processor,
                        dispatch.Error ?? "Échec inconnu du processeur.", activityAuthor);
                    return;
                }
                result = dispatch.Result;
                var required = processor.RequiredSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!required.IsSubsetOf(result.SkillsUsed.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                {
                    var missing = required.Except(result.SkillsUsed, StringComparer.OrdinalIgnoreCase);
                    await _executions.FailAttemptAsync(slug, execution, processor,
                        $"Skills obligatoires non exécutés : {string.Join(", ", missing)}.", activityAuthor);
                    return;
                }
                await _executions.SaveAgentResultAsync(slug, execution, result);
            }

            if (execution.CapitalizationStatus is not (MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange))
            {
                var capitalization = await _memory.CapitalizeAsync(
                    slug, processor.ColumnId, execution.Id, result.Lessons, cancellationToken);
                if (capitalization.Status == MemoryCapitalizationStatus.Failed)
                {
                    await _executions.SetCapitalizationAsync(slug, execution,
                        MemoryCapitalizationStatus.RetryRequired, capitalization.Error);
                    await _executions.FailAttemptAsync(slug, execution, processor,
                        $"Capitalisation de la mémoire en échec : {capitalization.Error}", activityAuthor);
                    return;
                }
                await _executions.SetCapitalizationAsync(slug, execution, capitalization.Status);

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
                                $"{execution.Id}-feedback-{upstream.Id}", feedback, cancellationToken);
                            if (attributed.Status == MemoryCapitalizationStatus.Failed)
                            {
                                await _executions.SetCapitalizationAsync(slug, execution,
                                    MemoryCapitalizationStatus.RetryRequired, attributed.Error);
                                await _executions.FailAttemptAsync(slug, execution, processor,
                                    $"Attribution du retour aval en échec : {attributed.Error}", activityAuthor);
                                return;
                            }
                        }
                    }
                }
            }

            var contextRejection = await _executions.ValidateSuccessContextAsync(
                slug, execution, processor, result);
            if (contextRejection is not null)
            {
                await _executions.FailAttemptAsync(slug, execution, processor,
                    contextRejection, activityAuthor);
                return;
            }

            ticket = await _tickets.GetTicketAsync(slug, execution.TicketId)
                ?? throw new InvalidOperationException($"Le ticket #{execution.TicketId} n’existe plus.");
            if (hasSuccessRoute && await IsSuccessOutcomeAsync(slug, processor, result))
            {
                // Success-context validation and routing must win before any configured action
                // can emit an irreversible side effect. CompleteAsync performs the final check
                // and persists the transition atomically; rejected outcomes execute no action.
                if (BeforeFinalSuccessValidationAsync is not null)
                    await BeforeFinalSuccessValidationAsync();
                await _executions.CompleteAsync(slug, execution, processor, result, activityAuthor);
                var completed = (await _executions.ListAsync(slug, execution.TicketId))
                    .FirstOrDefault(item => item.Id == execution.Id);
                if (completed?.Status != ColumnExecutionStatus.Completed)
                    return;
                await ExecuteActionsAsync(
                    slug, processor, execution, ticket,
                    processor.BeforeActions.Concat(processor.AfterActions).ToList(), result,
                    activityAuthor, cancellationToken);
                return;
            }
            if (hasSuccessRoute && !await ExecuteActionsAsync(
                    slug, processor, execution, ticket, processor.BeforeActions, null,
                    activityAuthor, cancellationToken))
                return;
            if (!await ExecuteActionsAsync(
                    slug, processor, execution, ticket, processor.AfterActions, result,
                    activityAuthor, cancellationToken))
                return;
            await _executions.CompleteAsync(slug, execution, processor, result, activityAuthor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Column processor {ProcessorId} failed for ticket {TicketId}", processor.Id, execution.TicketId);
            await _executions.FailAttemptAsync(slug, execution, processor, ex.Message, processor.Name);
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
        string slug, ColumnProcessor processor, ColumnAgentResult result)
    {
        var targetId = processor.Routes.FirstOrDefault(route =>
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
        var completed = execution.CompletedActionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            if (completed.Contains(action.Id)) continue;
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
