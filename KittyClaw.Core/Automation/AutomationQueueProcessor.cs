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

                var statusBefore = ticket.Status;
                var latestCommentBefore = ticket.Comments
                    .Where(c => !string.Equals(c.Author, "automation", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Id).DefaultIfEmpty().Max();
                var run = await _executor.ExecuteAutomationToCompletionAsync(rt, snapshot, firing, ct);
                if (run is null)
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Completed, null, ct);
                    return;
                }
                var runIndex = snapshot.Actions.FindIndex(a => a is RunAgentActionSpec);
                if (runIndex >= 0 && runIndex < snapshot.Actions.Count - 1)
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Completed, null, ct);
                    return;
                }
                var updated = await _tickets.GetTicketAsync(slug, entry.TicketId);
                var inputChanged = updated is not null &&
                    (!string.Equals(statusBefore, updated.Status, StringComparison.OrdinalIgnoreCase) ||
                     updated.Comments
                         .Where(c => !string.Equals(c.Author, "automation", StringComparison.OrdinalIgnoreCase))
                         .Select(c => c.Id).DefaultIfEmpty().Max() != latestCommentBefore);

                if (inputChanged)
                {
                    if (updated is not null && string.Equals(statusBefore, updated.Status, StringComparison.OrdinalIgnoreCase))
                        await _queue.ScheduleRetryAsync(slug, entry.Id,
                            DateTime.UtcNow.AddSeconds(Math.Max(1, trigger.Seconds)), resetAttempts: true, ct);
                    else
                        await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Completed, null, ct);
                    return;
                }
                if (run?.Status == AgentRunStatus.Stopped)
                {
                    await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Cancelled,
                        "Agent run was stopped manually; automatic retry suppressed.", ct);
                    return;
                }
                var cap = Math.Max(1, trigger.MaxConsecutiveRuns);
                if (entry.Attempts >= cap)
                {
                    await ParkAtRetryCapAsync(slug, entry, cap, ct);
                    return;
                }
                var failed = run?.Status == AgentRunStatus.Failed;
                var delaySeconds = failed
                    ? ComputeFailureBackoffSeconds(trigger, entry.Attempts)
                    : Math.Max(1, trigger.Seconds);
                await _queue.ScheduleRetryAsync(slug, entry.Id,
                    DateTime.UtcNow.AddSeconds(delaySeconds), resetAttempts: false, ct);
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

    internal static int ComputeFailureBackoffSeconds(TicketInColumnTriggerSpec trigger, int attempts)
    {
        var initial = Math.Max(1, trigger.FailureBackoffSeconds);
        var maximum = Math.Max(initial, trigger.MaxFailureBackoffSeconds);
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        return (int)Math.Min(maximum, initial * Math.Pow(2, exponent));
    }

    private async Task ParkAtRetryCapAsync(string slug, AutomationQueueEntry entry, int cap, CancellationToken ct)
    {
        const string marker = "[automation-retry-cap]";
        var ticket = await _tickets.GetTicketAsync(slug, entry.TicketId);
        if (ticket is not null && !string.Equals(ticket.Status, "Blocked", StringComparison.OrdinalIgnoreCase))
            await _tickets.MoveTicketAsync(slug, entry.TicketId, "Blocked", "automation");
        ticket = await _tickets.GetTicketAsync(slug, entry.TicketId);
        if (ticket is not null && !ticket.Comments.Any(c => c.Content.Contains(marker, StringComparison.Ordinal)))
            await _tickets.AddCommentAsync(slug, entry.TicketId,
                $"{marker} Automation '{entry.AutomationName}' reached its cap of {cap} consecutive runs without a status change or new comment. Parked in Blocked to prevent a dispatch loop.",
                "automation");
        await _queue.FinishAsync(slug, entry.Id, AutomationQueueStatus.Cancelled,
            $"Consecutive run cap ({cap}) reached; ticket parked in Blocked.", ct);
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
