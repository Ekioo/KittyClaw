using System.Collections.Concurrent;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
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
    private readonly ILogger<ColumnProcessingEngine> _logger;
    private readonly ConcurrentDictionary<string, byte> _pendingProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _activeProcessors = new();
    private readonly SemaphoreSlim _wake = new(0);

    public ColumnProcessingEngine(
        ProjectService projects, TicketService tickets, ColumnProcessorService processors,
        ColumnExecutionService executions, IColumnAgentDispatcher dispatcher, ColumnActionExecutor actions,
        ILogger<ColumnProcessingEngine> logger)
    {
        _projects = projects;
        _tickets = tickets;
        _processors = processors;
        _executions = executions;
        _dispatcher = dispatcher;
        _actions = actions;
        _logger = logger;
        _tickets.TicketStatusChanged += OnTicketChanged;
        _tickets.TicketCreated += OnTicketCreated;
    }

    private void OnTicketChanged(string slug, int _, string __, string ___) => Signal(slug);
    private void OnTicketCreated(string slug, int _) => Signal(slug);

    public void Signal(string projectSlug)
    {
        _pendingProjects[projectSlug] = 0;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var projects = await _projects.ListProjectsAsync();
        foreach (var project in projects.Where(p => !p.IsPaused))
        {
            await _executions.RecoverInterruptedAsync(project.Slug);
            Signal(project.Slug);
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

    private async Task ProcessAsync(
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

            if (!await ExecuteActionsAsync(
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
                if (string.Equals(result.Outcome, "wait_for_children", StringComparison.OrdinalIgnoreCase))
                {
                    await _executions.CompleteAsync(slug, execution, processor, result, activityAuthor);
                    return;
                }
                await _executions.SaveAgentResultAsync(slug, execution, result);
            }

            ticket = await _tickets.GetTicketAsync(slug, execution.TicketId)
                ?? throw new InvalidOperationException($"Le ticket #{execution.TicketId} n’existe plus.");
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
        _wake.Dispose();
        base.Dispose();
    }
}
