using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using KittyClaw.Core.Automation.Triggers;
using KittyClaw.Core.Services;

namespace KittyClaw.Core.Automation;

internal sealed class AutomationQueueProcessor
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(35);
    private readonly ProjectService _projects;
    private readonly TicketService _tickets;
    private readonly AutomationQueueStore _queue;
    private readonly ProjectRuntimeManager _runtimes;
    private readonly ActionExecutor _executor;
    private readonly ILogger _logger;
    private const int MaxWorkersPerProject = 8;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectWorkers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reservationLock = new();
    private readonly Dictionary<string, Dictionary<long, GroupReservation>> _reservations =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record GroupReservation(HashSet<string> Groups, HashSet<string> Excludes);

    public AutomationQueueProcessor(ProjectService projects, TicketService tickets, AutomationQueueStore queue,
        ProjectRuntimeManager runtimes, ActionExecutor executor, ILogger logger)
    {
        _projects = projects; _tickets = tickets; _queue = queue; _runtimes = runtimes; _executor = executor; _logger = logger;
    }

    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        foreach (var project in await _projects.ListProjectsAsync())
        {
            if (ct.IsCancellationRequested) return;
            if (project.IsPaused) continue;
            var workers = _projectWorkers.GetOrAdd(project.Slug,
                _ => new SemaphoreSlim(MaxWorkersPerProject, MaxWorkersPerProject));
            while (workers.Wait(0))
            {
                AutomationQueueEntry? entry;
                try { entry = await _queue.ClaimNextAsync(project.Slug, Lease, ct); }
                catch
                {
                    workers.Release();
                    throw;
                }
                if (entry is null)
                {
                    workers.Release();
                    break;
                }
                var reservation = ResolveReservation(entry);
                if (!TryReserve(project.Slug, entry.Id, reservation))
                {
                    await _queue.RequeueAsync(project.Slug, entry.Id, ct);
                    workers.Release();
                    break;
                }
                _ = ProcessEntryAsync(project.Slug, entry, workers, ct);
            }
        }
    }

    private async Task ProcessEntryAsync(
        string slug, AutomationQueueEntry entry, SemaphoreSlim workers, CancellationToken ct)
    {
        try
        {
            try
            {
                await _runtimes.EnsureLoadedAsync(slug);
                var rt = _runtimes.GetRuntime(slug);
                var current = rt.Config?.Automations.FirstOrDefault(a =>
                    string.Equals(a.Id, entry.AutomationId, StringComparison.OrdinalIgnoreCase));
                if (current is null || !current.Enabled)
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Cancelled,
                        current is null ? "Automation was deleted." : "Automation was disabled.", ct);
                    return;
                }

                var snapshot = JsonSerializer.Deserialize<Automation>(entry.AutomationSnapshot, AutomationStore.JsonOptions)
                    ?? throw new InvalidOperationException("The queued automation snapshot is invalid.");
                var ticket = await _tickets.GetTicketAsync(slug, entry.TicketId);
                if (ticket is null)
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Cancelled, "Ticket was deleted.", ct);
                    return;
                }
                if (snapshot.Trigger is not TicketInColumnTriggerSpec trigger ||
                    !trigger.Columns.Contains(ticket.Status, StringComparer.OrdinalIgnoreCase))
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Skipped,
                        $"Ticket is now in '{ticket.Status}', outside the watched columns.", ct);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(trigger.AssigneeSlug) &&
                    !string.Equals(trigger.AssigneeSlug, ticket.AssignedTo, StringComparison.OrdinalIgnoreCase))
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Skipped,
                        "The ticket assignee no longer matches the trigger.", ct);
                    return;
                }
                var firing = new TriggerFiring(ticket.Id, ticket.Title, ticket.Status);
                if (!await _executor.ConditionsMatchAsync(rt, snapshot, firing))
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Skipped,
                        "Automation conditions no longer match.", ct);
                    return;
                }

                await _executor.ExecuteAutomationToCompletionAsync(rt, snapshot, firing, ct);
                await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Completed, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queued automation {Id} failed for ticket #{Ticket}", entry.AutomationId, entry.TicketId);
                await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Failed, ex.Message, CancellationToken.None);
            }
        }
        finally
        {
            ReleaseReservation(slug, entry.Id);
            workers.Release();
        }
    }

    private static GroupReservation ResolveReservation(AutomationQueueEntry entry)
    {
        var automation = JsonSerializer.Deserialize<Automation>(entry.AutomationSnapshot, AutomationStore.JsonOptions);
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (automation is null) return new(groups, excludes);
        foreach (var action in automation.Actions.OfType<RunAgentActionSpec>())
        {
            var agent = action.Agent;
            var group = string.IsNullOrWhiteSpace(action.ConcurrencyGroup) ? agent : action.ConcurrencyGroup;
            groups.Add(ResolveGroup(group, agent, entry.TicketId));
            foreach (var excluded in action.MutuallyExclusiveWith)
                excludes.Add(ResolveGroup(excluded, agent, entry.TicketId));
        }
        return new(groups, excludes);
    }

    private static string ResolveGroup(string value, string agent, int ticketId) => value
        .Replace("{assignee}", agent)
        .Replace("{ticketId}", ticketId.ToString());

    private bool TryReserve(string slug, long entryId, GroupReservation candidate)
    {
        lock (_reservationLock)
        {
            if (!_reservations.TryGetValue(slug, out var active))
                _reservations[slug] = active = [];
            if (active.Values.Any(existing =>
                    existing.Groups.Overlaps(candidate.Groups) ||
                    existing.Groups.Overlaps(candidate.Excludes) ||
                    existing.Excludes.Overlaps(candidate.Groups)))
                return false;
            active[entryId] = candidate;
            return true;
        }
    }

    private void ReleaseReservation(string slug, long entryId)
    {
        lock (_reservationLock)
        {
            if (!_reservations.TryGetValue(slug, out var active)) return;
            active.Remove(entryId);
            if (active.Count == 0) _reservations.Remove(slug);
        }
    }
}
