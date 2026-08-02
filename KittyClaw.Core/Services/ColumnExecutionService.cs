using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Durable ticket claims and lifecycle for column processors.</summary>
public sealed class ColumnExecutionService(ProjectService projects, TicketService tickets)
{
    private static async Task EnsureTableAsync(TodoDbContext db)
    {
        await MigrationGate.RunOnceAsync(db, "column-executions-v1", static d =>
            d.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ColumnExecutions (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ProcessorId INTEGER NOT NULL,
                    TicketId INTEGER NOT NULL,
                    Status INTEGER NOT NULL,
                    Attempt INTEGER NOT NULL DEFAULT 1,
                    ClaimedAt TEXT NOT NULL,
                    AvailableAt TEXT NULL,
                    EndedAt TEXT NULL,
                    RunId TEXT NULL,
                    Outcome TEXT NULL,
                    Summary TEXT NULL,
                    Error TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ColumnExecutions_ActiveTicket
                    ON ColumnExecutions(TicketId)
                    WHERE Status IN (0, 1, 2, 4);
                CREATE INDEX IF NOT EXISTS IX_ColumnExecutions_ProcessorStatus
                    ON ColumnExecutions(ProcessorId, Status, AvailableAt);
                """));
    }

    public async Task<ColumnExecution?> ClaimNextAsync(string projectSlug, ColumnProcessor processor, DateTime now)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await EnsureTableAsync(db);

        var waiting = await db.ColumnExecutions
            .Where(e => e.ProcessorId == processor.Id && e.Status == ColumnExecutionStatus.WaitingForChildren)
            .OrderBy(e => e.ClaimedAt).ToListAsync();
        foreach (var candidate in waiting)
        {
            var childColumnIds = await db.Tickets
                .Where(t => t.ParentId == candidate.TicketId && t.BlocksParent)
                .Select(t => t.ColumnId).Distinct().ToListAsync();
            if (childColumnIds.Count == 0) continue;
            var successCount = await db.BoardColumns.CountAsync(c => childColumnIds.Contains(c.Id) && c.Role == ColumnRole.Success);
            if (successCount != childColumnIds.Count) continue;
            candidate.Status = ColumnExecutionStatus.Running;
            candidate.Attempt++;
            candidate.Error = null;
            await db.SaveChangesAsync();
            return candidate;
        }

        // Retries keep their original durable claim and take precedence over new work.
        var retry = await db.ColumnExecutions
            .Where(e => e.ProcessorId == processor.Id && e.Status == ColumnExecutionStatus.Retrying
                && (e.AvailableAt == null || e.AvailableAt <= now))
            .OrderBy(e => e.AvailableAt).ThenBy(e => e.ClaimedAt)
            .FirstOrDefaultAsync();
        if (retry is not null)
        {
            retry.Status = ColumnExecutionStatus.Running;
            retry.Attempt++;
            retry.AvailableAt = null;
            retry.Error = null;
            await db.SaveChangesAsync();
            return retry;
        }

        var activeTicketIds = db.ColumnExecutions
            .Where(e => e.Status == ColumnExecutionStatus.Running
                || e.Status == ColumnExecutionStatus.Retrying
                || e.Status == ColumnExecutionStatus.WaitingForChildren
                || e.Status == ColumnExecutionStatus.Failed)
            .Select(e => e.TicketId);
        var candidates = db.Tickets.Where(t => t.ColumnId == processor.ColumnId && !activeTicketIds.Contains(t.Id));
        candidates = processor.SelectionOrder switch
        {
            TicketSelectionOrder.PriorityThenPosition => candidates.OrderByDescending(t => t.Priority).ThenBy(t => t.SortOrder).ThenBy(t => t.CreatedAt),
            TicketSelectionOrder.Oldest => candidates.OrderBy(t => t.CreatedAt),
            TicketSelectionOrder.Newest => candidates.OrderByDescending(t => t.CreatedAt),
            _ => candidates.OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt),
        };

        // Read a small window: claimed or waiting parents must not stall later eligible work.
        var window = await candidates.Take(50).ToListAsync();
        Ticket? selected = null;
        foreach (var candidate in window)
        {
            var blockingChildren = await db.Tickets.Where(t => t.ParentId == candidate.Id && t.BlocksParent)
                .Select(t => t.ColumnId).Distinct().ToListAsync();
            if (blockingChildren.Count == 0)
            {
                selected = candidate;
                break;
            }
            var successful = await db.BoardColumns.CountAsync(c => blockingChildren.Contains(c.Id) && c.Role == ColumnRole.Success);
            if (successful == blockingChildren.Count)
            {
                selected = candidate;
                break;
            }
        }
        if (selected is null) return null;

        var execution = new ColumnExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            ProcessorId = processor.Id,
            TicketId = selected.Id,
            Status = ColumnExecutionStatus.Running,
            ClaimedAt = now,
        };
        db.ColumnExecutions.Add(execution);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Another engine worker won the unique active-ticket claim.
            return null;
        }
        return execution;
    }

    public async Task SetRunIdAsync(string projectSlug, string executionId, string runId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var execution = await db.ColumnExecutions.FindAsync(executionId);
        if (execution is null) return;
        execution.RunId = runId;
        await db.SaveChangesAsync();
    }

    public async Task CompleteAsync(
        string projectSlug, ColumnExecution execution, ColumnProcessor processor,
        ColumnAgentResult result, string author)
    {
        var required = processor.RequiredSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!required.IsSubsetOf(result.SkillsUsed.ToHashSet(StringComparer.OrdinalIgnoreCase)))
        {
            var missing = required.Except(result.SkillsUsed, StringComparer.OrdinalIgnoreCase);
            await FailAttemptAsync(projectSlug, execution, processor,
                $"Skills obligatoires non exécutés : {string.Join(", ", missing)}.", author);
            return;
        }

        if (string.Equals(result.Outcome, "wait_for_children", StringComparison.OrdinalIgnoreCase))
        {
            await using var waitingDb = projects.GetProjectDb(projectSlug);
            await EnsureTableAsync(waitingDb);
            var blocking = await waitingDb.Tickets.AnyAsync(t => t.ParentId == execution.TicketId && t.BlocksParent);
            if (!blocking)
            {
                await FailAttemptAsync(projectSlug, execution, processor,
                    "Le processeur demande d'attendre des sous-tickets, mais aucun sous-ticket bloquant n'existe.", author);
                return;
            }
            var waitingRow = await waitingDb.ColumnExecutions.FindAsync(execution.Id);
            if (waitingRow is null) return;
            waitingRow.Status = ColumnExecutionStatus.WaitingForChildren;
            waitingRow.Outcome = result.Outcome;
            waitingRow.Summary = result.Summary;
            await waitingDb.SaveChangesAsync();
            return;
        }

        var route = processor.Routes.FirstOrDefault(r =>
            string.Equals(r.Outcome, result.Outcome, StringComparison.OrdinalIgnoreCase));
        var targetId = route?.TargetColumnId ?? processor.DefaultTargetColumnId;
        if (targetId is null)
        {
            await FailAttemptAsync(projectSlug, execution, processor,
                $"Aucune route pour l'issue '{result.Outcome}' et aucune destination par défaut.", author);
            return;
        }

        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await EnsureTableAsync(db);
        var target = await db.BoardColumns.FindAsync(targetId.Value)
            ?? throw new InvalidOperationException($"La colonne cible #{targetId} n'existe plus.");
        var ticket = await db.Tickets.FindAsync(execution.TicketId)
            ?? throw new InvalidOperationException($"Le ticket #{execution.TicketId} n'existe plus.");
        var oldStatus = ticket.Status;
        ticket.PipelineId = target.PipelineId;
        ticket.ColumnId = target.Id;
        ticket.Status = target.Name;
        ticket.UpdatedAt = DateTime.UtcNow;
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        if (row is null) return;
        row.Status = ColumnExecutionStatus.Completed;
        row.Outcome = result.Outcome;
        row.Summary = result.Summary;
        row.EndedAt = DateTime.UtcNow;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = author,
            Text = $"traitement de colonne terminé ({result.Outcome}) : {oldStatus} → {target.Name}",
        });
        await db.SaveChangesAsync();
        tickets.NotifyStatusChanged(projectSlug, ticket.Id, oldStatus, target.Name);
    }

    public async Task FailAttemptAsync(
        string projectSlug, ColumnExecution execution, ColumnProcessor processor,
        string error, string author)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        if (row is null) return;
        row.Error = error;
        if (row.Attempt < processor.MaxAttempts)
        {
            row.Status = ColumnExecutionStatus.Retrying;
            var factor = Math.Pow(2, Math.Max(0, row.Attempt - 1));
            row.AvailableAt = DateTime.UtcNow.AddSeconds(Math.Min(86400, processor.RetryBackoffSeconds * factor));
            await db.SaveChangesAsync();
            return;
        }

        row.Status = ColumnExecutionStatus.Failed;
        row.EndedAt = DateTime.UtcNow;
        int? movedTicketId = null;
        string? movedFrom = null;
        string? movedTo = null;
        if (processor.TechnicalFailureColumnId is int failureColumnId)
        {
            var target = await db.BoardColumns.FindAsync(failureColumnId);
            var ticket = await db.Tickets.FindAsync(row.TicketId);
            if (target is not null && ticket is not null)
            {
                var oldStatus = ticket.Status;
                ticket.PipelineId = target.PipelineId;
                ticket.ColumnId = target.Id;
                ticket.Status = target.Name;
                ticket.UpdatedAt = DateTime.UtcNow;
                // The failed execution is terminal once the ticket has been routed.
                // Keeping it in Failed would retain the active-ticket lock and prevent
                // the destination column processor from claiming the ticket.
                row.Status = ColumnExecutionStatus.Completed;
                row.Outcome = "technical_failure";
                movedTicketId = ticket.Id;
                movedFrom = oldStatus;
                movedTo = target.Name;
                db.ActivityEntries.Add(new ActivityEntry
                {
                    TicketId = ticket.Id,
                    Author = author,
                    Text = $"traitement de colonne en échec après {row.Attempt} tentative(s) : {oldStatus} → {target.Name}",
                });
            }
        }
        await db.SaveChangesAsync();
        if (movedTicketId is int ticketId)
            tickets.NotifyStatusChanged(projectSlug, ticketId, movedFrom!, movedTo!);
    }

    public async Task<List<ColumnExecution>> ListAsync(string projectSlug, int? ticketId = null)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var query = db.ColumnExecutions.AsNoTracking().AsQueryable();
        if (ticketId is not null) query = query.Where(e => e.TicketId == ticketId);
        return await query.OrderByDescending(e => e.ClaimedAt).Take(200).ToListAsync();
    }

    public async Task RecoverInterruptedAsync(string projectSlug)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var interrupted = await db.ColumnExecutions.Where(e => e.Status == ColumnExecutionStatus.Running).ToListAsync();
        foreach (var execution in interrupted)
        {
            execution.Status = ColumnExecutionStatus.Retrying;
            // ClaimNext increments attempts for a retry. Compensate here so a host
            // restart resumes the interrupted attempt instead of consuming a new one.
            execution.Attempt = Math.Max(0, execution.Attempt - 1);
            execution.AvailableAt = DateTime.UtcNow;
            execution.Error = "Exécution interrompue par un arrêt du moteur.";
        }
        await db.SaveChangesAsync();
    }

    public async Task<bool> RetryAsync(string projectSlug, string executionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var execution = await db.ColumnExecutions.FindAsync(executionId);
        if (execution is null || execution.Status != ColumnExecutionStatus.Failed) return false;
        execution.Status = ColumnExecutionStatus.Retrying;
        execution.Attempt = 0;
        execution.AvailableAt = DateTime.UtcNow;
        execution.EndedAt = null;
        execution.Error = null;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> CancelAsync(string projectSlug, string executionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var execution = await db.ColumnExecutions.FindAsync(executionId);
        if (execution is null || execution.Status is ColumnExecutionStatus.Completed or ColumnExecutionStatus.Cancelled)
            return false;
        execution.Status = ColumnExecutionStatus.Cancelled;
        execution.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }
}
