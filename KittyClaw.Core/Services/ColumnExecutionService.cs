using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KittyClaw.Core.Services;

/// <summary>Durable ticket claims and lifecycle for column processors.</summary>
public sealed class ColumnExecutionService(ProjectService projects, TicketService tickets)
{
    internal Func<Task>? BeforeSuccessCompareAndSwapAsync { get; set; }
    internal static readonly TimeSpan RoutingLoopWindow = TimeSpan.FromMinutes(10);
    internal const string RoutingLoopError =
        "Protection anti-boucle : cette transition a été répétée sans progrès observable dans les 10 dernières minutes.";

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
                    Error TEXT NULL,
                    TargetColumnId INTEGER NULL,
                    CompletedActionIdsJson TEXT NOT NULL DEFAULT '[]',
                    CurrentActionId TEXT NULL,
                    AgentCompleted INTEGER NOT NULL DEFAULT 0,
                    AgentResultJson TEXT NULL,
                    ProgressFingerprint TEXT NULL,
                    ProgressSignalsJson TEXT NOT NULL DEFAULT '[]',
                    LoopDiagnosticJson TEXT NULL,
                    TriggerTicketUpdatedAt TEXT NULL,
                    TriggerSignalType TEXT NOT NULL DEFAULT 'column_scan',
                    TriggerOwnerCommentId INTEGER NULL,
                    TriggerOwnerCommentCreatedAt TEXT NULL,
                    ConsumedTicketUpdatedAt TEXT NULL,
                    ConsumedOwnerCommentId INTEGER NULL,
                    ContextRejectionReason TEXT NULL,
                    CapitalizationStatus INTEGER NOT NULL DEFAULT 0,
                    CapitalizationError TEXT NULL,
                    CapitalizedAt TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_ColumnExecutions_ActiveTicket
                    ON ColumnExecutions(TicketId)
                    WHERE Status IN (0, 1, 2, 4);
                CREATE INDEX IF NOT EXISTS IX_ColumnExecutions_ProcessorStatus
                    ON ColumnExecutions(ProcessorId, Status, AvailableAt);
                """));
        await MigrationGate.RunOnceAsync(db, "column-executions-actions-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN CompletedActionIdsJson TEXT NOT NULL DEFAULT '[]'");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN CurrentActionId TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN AgentCompleted INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN AgentResultJson TEXT NULL");
        });
        await MigrationGate.RunOnceAsync(db, "column-executions-routing-loop-v1", static d =>
            MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN TargetColumnId INTEGER NULL"));
        await MigrationGate.RunOnceAsync(db, "column-executions-progress-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN ProgressFingerprint TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN ProgressSignalsJson TEXT NOT NULL DEFAULT '[]'");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN LoopDiagnosticJson TEXT NULL");
        });
        await MigrationGate.RunOnceAsync(db, "column-executions-fresh-context-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN TriggerTicketUpdatedAt TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN TriggerSignalType TEXT NOT NULL DEFAULT 'column_scan'");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN TriggerOwnerCommentId INTEGER NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN TriggerOwnerCommentCreatedAt TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN ConsumedTicketUpdatedAt TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN ConsumedOwnerCommentId INTEGER NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN ContextRejectionReason TEXT NULL");
        });
        await MigrationGate.RunOnceAsync(db, "column-executions-capitalization-v1", static async d =>
        {
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN CapitalizationStatus INTEGER NOT NULL DEFAULT 0");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN CapitalizationError TEXT NULL");
            await MigrationGate.AddColumnIfMissingAsync(d, "ALTER TABLE ColumnExecutions ADD COLUMN CapitalizedAt TEXT NULL");
        });
    }

    public async Task<ColumnExecution?> ClaimNextAsync(string projectSlug, ColumnProcessor processor, DateTime now,
        IReadOnlyDictionary<int, int>? ownerFeedbackSignals = null)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        // ClaimNext queries the full Ticket entity directly. Legacy project databases must
        // receive newly added ticket columns before EF prepares that query; the board's
        // TicketService migration may not have run yet during engine startup.
        await TicketService.EnsureAgentUsageColumnsAsync(db);
        await EnsureTableAsync(db);

        var waiting = await db.ColumnExecutions
            .Where(e => e.ProcessorId == processor.Id && e.Status == ColumnExecutionStatus.WaitingForChildren)
            .OrderBy(e => e.ClaimedAt).ToListAsync();
        foreach (var candidate in waiting)
        {
            var childColumnIds = await db.Tickets
                .Where(t => t.ParentId == candidate.TicketId && t.BlocksParent)
                .Select(t => t.ColumnId).Distinct().ToListAsync();
            var terminalCount = await db.BoardColumns.CountAsync(c => childColumnIds.Contains(c.Id)
                && (c.Role == ColumnRole.Success || c.Role == ColumnRole.Failure));
            if (terminalCount != childColumnIds.Count) continue;
            candidate.Status = ColumnExecutionStatus.Running;
            candidate.Attempt++;
            // The previous agent result is necessarily `wait_for_children`. Replaying that
            // durable checkpoint after the children have finished would put the execution
            // straight back into WaitingForChildren and create a tight claim/complete loop.
            // Clear the agent checkpoint so the processor resumes with the completed child
            // context and can choose its next route. Completed actions stay checkpointed.
            candidate.AgentCompleted = false;
            candidate.AgentResult = null;
            candidate.Outcome = null;
            candidate.Summary = null;
            candidate.CapitalizationStatus = MemoryCapitalizationStatus.Pending;
            candidate.CapitalizationError = null;
            candidate.CapitalizedAt = null;
            candidate.Error = null;
            await db.SaveChangesAsync();
            return candidate;
        }

        // Retries keep their original durable claim and take precedence over new work. Revalidate
        // their trigger context in the same transaction that claims them: a delayed retry must not
        // dispatch an agent after the ticket has moved or otherwise changed since the failed attempt.
        await using var retryTransaction = await db.Database.BeginTransactionAsync();
        var retries = await db.ColumnExecutions
            .Where(e => e.ProcessorId == processor.Id && e.Status == ColumnExecutionStatus.Retrying
                && (e.AvailableAt == null || e.AvailableAt <= now))
            .OrderBy(e => e.AvailableAt).ThenBy(e => e.ClaimedAt)
            .ToListAsync();
        foreach (var retry in retries)
        {
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket =>
                ticket.Id == retry.TicketId && ticket.ColumnId == processor.ColumnId);
            var triggerIsCurrent = ticket is not null
                && (retry.TriggerTicketUpdatedAt == null
                    || ticket.UpdatedAt == retry.TriggerTicketUpdatedAt
                    // A completed processor may have advanced UpdatedAt itself by publishing
                    // its evidence before a restart. The durable result is authoritative only
                    // when it names the exact current version; any later mutation still rejects it.
                    || retry.AgentCompleted
                        && retry.AgentResult?.Evidence?.TicketUpdatedAt is DateTime consumedVersion
                        && consumedVersion.ToUniversalTime() == ticket.UpdatedAt.ToUniversalTime()
                    // The agent's own delivery or progress comments during a failed attempt also
                    // advance UpdatedAt. Cancelling the retry for that discarded completed business
                    // work and re-triggered the ticket from scratch (#1497/#1508); only foreign
                    // mutations — owner feedback, field edits — actually invalidate the context.
                    || await AdvancedOnlyByRunCommentsAsync(db, retry, ticket));
            if (!triggerIsCurrent)
            {
                retry.Status = ColumnExecutionStatus.Cancelled;
                retry.AvailableAt = null;
                retry.EndedAt = DateTime.UtcNow;
                retry.ContextRejectionReason = "stale_trigger_context";
                retry.Error = "stale_trigger_context: le ticket a quitté la colonne déclencheuse ou son contexte a changé.";
                continue;
            }
            retry.Status = ColumnExecutionStatus.Running;
            retry.Attempt++;
            retry.AvailableAt = null;
            retry.PreviousAttemptError = retry.Error;
            retry.Error = null;
            await db.SaveChangesAsync();
            await retryTransaction.CommitAsync();
            return retry;
        }
        await db.SaveChangesAsync();
        await retryTransaction.CommitAsync();

        var activeTicketIds = db.ColumnExecutions
            .Where(e => e.Status == ColumnExecutionStatus.Running
                || e.Status == ColumnExecutionStatus.Retrying
                || e.Status == ColumnExecutionStatus.WaitingForChildren
                || e.Status == ColumnExecutionStatus.Failed)
            .Select(e => e.TicketId);
        // A ticket with FireAt is parked durably until ScheduledPromotionService moves it to its
        // wake target. Re-claiming it in the waiting column would turn every persisted schedule
        // into a tight column-scan loop and eventually trip the routing-loop guard.
        var candidates = db.Tickets.Where(t => t.ColumnId == processor.ColumnId
            && t.FireAt == null
            && !activeTicketIds.Contains(t.Id));
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
            var terminal = await db.BoardColumns.CountAsync(c => blockingChildren.Contains(c.Id)
                && (c.Role == ColumnRole.Success || c.Role == ColumnRole.Failure));
            if (terminal == blockingChildren.Count)
            {
                selected = candidate;
                break;
            }
        }
        if (selected is null) return null;

        // A route A -> B that occurs twice for the same ticket within a short window means
        // the ticket has completed at least one full cycle and has returned to A. Stop before
        // launching B again: unlike an in-column retry, changing columns used to reset every
        // attempt counter and could therefore dispatch agents forever.
        var recentCompleted = await db.ColumnExecutions.AsNoTracking()
            .Where(e => e.TicketId == selected.Id
                && e.Status == ColumnExecutionStatus.Completed
                && e.EndedAt >= now.Subtract(RoutingLoopWindow))
            .OrderByDescending(e => e.EndedAt)
            .ToListAsync();
        var recentProcessorIds = recentCompleted.Select(e => e.ProcessorId).Distinct().ToList();
        var sourceProcessors = await db.ColumnProcessors.AsNoTracking()
            .Where(p => recentProcessorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
        var incomingExecutions = recentCompleted.Where(e =>
            e.TargetColumnId == processor.ColumnId
            || (e.TargetColumnId is null
                && sourceProcessors.TryGetValue(e.ProcessorId, out var historicalProcessor)
                && ResolveHistoricalTarget(historicalProcessor, e.Outcome) == processor.ColumnId))
            .ToList();
        var incoming = incomingExecutions.FirstOrDefault();
        if (incoming is not null)
        {
            var comparable = incomingExecutions.Where(e => e.ProcessorId == incoming.ProcessorId)
                .OrderByDescending(e => e.EndedAt).Take(3).ToList();
            var repeatedFingerprint = comparable.Count >= 2
                && !string.IsNullOrWhiteSpace(comparable[0].ProgressFingerprint)
                && string.Equals(comparable[0].ProgressFingerprint, comparable[1].ProgressFingerprint,
                    StringComparison.Ordinal);
            // Summaries are useful diagnostics, but an agent can paraphrase the same verdict on
            // every pass. After two complete round trips, require a durable progress signal
            // (human/agent comment or completed action) before dispatching the same route again.
            // This catches A -> B -> A ping-pong loops whose wording changes just enough to evade
            // the exact-fingerprint guard.
            var repeatedWithoutMaterialEvidence = comparable.Count == 3
                && comparable.All(execution => !HasMaterialProgressSignal(execution.ProgressSignalsJson));
            if (repeatedFingerprint || repeatedWithoutMaterialEvidence)
            {
                var sourceProcessor = await db.ColumnProcessors.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == incoming.ProcessorId);
                var sourceColumn = sourceProcessor is null
                    ? null
                    : await db.BoardColumns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceProcessor.ColumnId);
                var diagnostic = new
                {
                    windowMinutes = RoutingLoopWindow.TotalMinutes,
                    sourceColumnId = sourceProcessor?.ColumnId,
                    targetColumnId = processor.ColumnId,
                    comparedExecutionIds = comparable.Select(e => e.Id).ToArray(),
                    fingerprint = comparable[0].ProgressFingerprint,
                    signals = JsonSerializer.Deserialize<JsonElement>(comparable[0].ProgressSignalsJson),
                    reason = repeatedFingerprint
                        ? "same_transition_and_progress_fingerprint"
                        : "repeated_transition_without_material_evidence"
                };
                var protectedExecution = new ColumnExecution
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ProcessorId = processor.Id,
                    TicketId = selected.Id,
                    Status = ColumnExecutionStatus.Failed,
                    Attempt = 0,
                    ClaimedAt = now,
                    EndedAt = now,
                    Outcome = "routing_loop",
                    Error = RoutingLoopError,
                    TargetColumnId = null,
                    ProgressFingerprint = comparable[0].ProgressFingerprint,
                    ProgressSignalsJson = comparable[0].ProgressSignalsJson,
                    LoopDiagnosticJson = JsonSerializer.Serialize(diagnostic),
                };
                db.ColumnExecutions.Add(protectedExecution);
                var transition = $"{sourceColumn?.Name ?? "colonne précédente"} → {selected.Status}";
                var oldStatus = selected.Status;
                db.ActivityEntries.Add(new ActivityEntry
                {
                    TicketId = selected.Id,
                    Author = processor.Name,
                    Text = $"protection anti-boucle : transition {transition} répétée sans progrès " +
                        $"(fenêtre 10 min, exécutions {string.Join(", ", comparable.Select(e => e.Id))}, " +
                        $"empreinte {comparable[0].ProgressFingerprint}); ticket maintenu dans {oldStatus}, reprise manuelle disponible",
                });
                await db.SaveChangesAsync();
                return null;
            }
        }

        Comment? triggerOwnerComment = null;
        if (ownerFeedbackSignals?.TryGetValue(selected.Id, out var ownerCommentId) == true)
            triggerOwnerComment = await db.Comments.AsNoTracking().FirstOrDefaultAsync(comment =>
                comment.Id == ownerCommentId && comment.TicketId == selected.Id && comment.Author == "owner");
        var execution = new ColumnExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            ProcessorId = processor.Id,
            TicketId = selected.Id,
            Status = ColumnExecutionStatus.Running,
            ClaimedAt = now,
            TriggerTicketUpdatedAt = selected.UpdatedAt,
            TriggerSignalType = triggerOwnerComment is null ? "column_scan" : "owner-feedback",
            TriggerOwnerCommentId = triggerOwnerComment?.Id,
            TriggerOwnerCommentCreatedAt = triggerOwnerComment?.CreatedAt,
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

    /// <summary>True when the ticket's UpdatedAt advance is fully explained by non-owner
    /// comments posted since this execution was claimed — i.e. the run's own delivery or
    /// progress comments. Any owner comment in that window, or an UpdatedAt that does not
    /// match the latest run comment (another field was edited afterwards), keeps the retry
    /// stale. Same sub-second tolerance as the delivery-comment check in
    /// ValidateSuccessContextAsync: the comment row and UpdatedAt are written microseconds
    /// apart in the same save.</summary>
    private static async Task<bool> AdvancedOnlyByRunCommentsAsync(
        TodoDbContext db, ColumnExecution retry, Ticket ticket)
    {
        var runComments = await db.Comments.AsNoTracking()
            .Where(comment => comment.TicketId == ticket.Id && comment.CreatedAt >= retry.ClaimedAt)
            .OrderByDescending(comment => comment.CreatedAt)
            .ToListAsync();
        if (runComments.Count == 0) return false;
        if (runComments.Any(comment => comment.Author == "owner")) return false;
        return Math.Abs((ticket.UpdatedAt - runComments[0].CreatedAt).TotalSeconds) < 1;
    }

    private static int? ResolveHistoricalTarget(ColumnProcessor processor, string? outcome)
    {
        if (string.Equals(outcome, "technical_failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "routing_loop", StringComparison.OrdinalIgnoreCase))
            return processor.TechnicalFailureColumnId;
        if (string.Equals(outcome, "action_failure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "action_interrupted", StringComparison.OrdinalIgnoreCase))
            return null;
        return processor.Routes.FirstOrDefault(route =>
            string.Equals(route.Outcome, outcome, StringComparison.OrdinalIgnoreCase))?.TargetColumnId
            ?? processor.DefaultTargetColumnId;
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

    public async Task BeginActionAsync(string projectSlug, ColumnExecution execution, string actionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id)
            ?? throw new InvalidOperationException($"L’exécution '{execution.Id}' n’existe plus.");
        row.CurrentActionId = actionId;
        await db.SaveChangesAsync();
        execution.CurrentActionId = actionId;
    }

    public async Task CompleteActionAsync(string projectSlug, ColumnExecution execution, string actionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id)
            ?? throw new InvalidOperationException($"L’exécution '{execution.Id}' n’existe plus.");
        var completed = row.CompletedActionIds;
        if (!completed.Contains(actionId, StringComparer.OrdinalIgnoreCase)) completed.Add(actionId);
        row.CompletedActionIds = completed;
        row.CurrentActionId = null;
        await db.SaveChangesAsync();
        execution.CompletedActionIds = completed;
        execution.CurrentActionId = null;
    }

    public async Task ClearCurrentActionAsync(string projectSlug, ColumnExecution execution)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        if (row is null) return;
        row.CurrentActionId = null;
        await db.SaveChangesAsync();
        execution.CurrentActionId = null;
    }

    public async Task SaveAgentResultAsync(
        string projectSlug, ColumnExecution execution, ColumnAgentResult result)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id)
            ?? throw new InvalidOperationException($"L’exécution '{execution.Id}' n’existe plus.");
        row.AgentCompleted = true;
        row.AgentResult = result;
        await db.SaveChangesAsync();
        execution.AgentCompleted = true;
        execution.AgentResult = result;
    }

    public async Task SetCapitalizationAsync(string projectSlug, ColumnExecution execution,
        MemoryCapitalizationStatus status, string? error = null)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id)
            ?? throw new InvalidOperationException($"L’exécution '{execution.Id}' n’existe plus.");
        row.CapitalizationStatus = status;
        row.CapitalizationError = error;
        row.CapitalizedAt = status is MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange
            ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        execution.CapitalizationStatus = row.CapitalizationStatus;
        execution.CapitalizationError = row.CapitalizationError;
        execution.CapitalizedAt = row.CapitalizedAt;
    }

    public async Task<ColumnExecution?> FindUpstreamExecutionAsync(
        string projectSlug, int ticketId, int destinationColumnId, string excludingExecutionId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await EnsureTableAsync(db);
        return await db.ColumnExecutions.AsNoTracking()
            .Where(e => e.TicketId == ticketId && e.Id != excludingExecutionId
                && e.Status == ColumnExecutionStatus.Completed && e.TargetColumnId == destinationColumnId)
            .OrderByDescending(e => e.EndedAt).FirstOrDefaultAsync();
    }

    public async Task<int?> FindProcessorColumnIdAsync(string projectSlug, int processorId)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnProcessorService.EnsureTableAsync(db);
        return await db.ColumnProcessors.AsNoTracking().Where(p => p.Id == processorId)
            .Select(p => (int?)p.ColumnId).FirstOrDefaultAsync();
    }

    public async Task RouteActionFailureAsync(
        string projectSlug, ColumnExecution execution, ColumnProcessor processor,
        ColumnProcessorAction action, string error, string author, bool outcomeUncertain = false)
    {
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await TicketService.EnsureActivityTableAsync(db);
        await EnsureTableAsync(db);
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        if (row is null) return;
        row.Error = error;
        row.CurrentActionId = null;
        row.EndedAt = DateTime.UtcNow;
        var targetId = action.FailureTargetColumnId ?? processor.TechnicalFailureColumnId;
        if (targetId is null)
        {
            // Holding a terminal Failed claim prevents the same ticket from being silently
            // selected again in this column. A user can explicitly retry it later.
            row.Status = ColumnExecutionStatus.Failed;
            await db.SaveChangesAsync();
            return;
        }

        var target = await db.BoardColumns.FindAsync(targetId.Value)
            ?? throw new InvalidOperationException($"La colonne d’échec #{targetId} n’existe plus.");
        if (target.Id == processor.ColumnId)
            throw new InvalidOperationException("Une action en échec ne peut pas renvoyer vers sa propre colonne.");
        var ticket = await db.Tickets.FindAsync(row.TicketId)
            ?? throw new InvalidOperationException($"Le ticket #{row.TicketId} n’existe plus.");
        var oldStatus = ticket.Status;
        var source = await db.BoardColumns.FindAsync(processor.ColumnId);
        ColumnAssignmentPolicy.Apply(ticket, source, target);
        ticket.PipelineId = target.PipelineId;
        ticket.ColumnId = target.Id;
        ticket.Status = target.Name;
        ticket.UpdatedAt = DateTime.UtcNow;
        row.Status = ColumnExecutionStatus.Completed;
        row.Outcome = outcomeUncertain ? "action_interrupted" : "action_failure";
        row.TargetColumnId = target.Id;
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = author,
            Text = outcomeUncertain
                ? $"action '{action.Id}' interrompue avec résultat incertain : {oldStatus} → {target.Name}"
                : $"action '{action.Id}' en échec : {oldStatus} → {target.Name}",
        });
        await db.SaveChangesAsync();
        tickets.NotifyStatusChanged(projectSlug, ticket.Id, oldStatus, target.Name);
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

        var isScheduled = string.Equals(result.Outcome, "scheduled", StringComparison.OrdinalIgnoreCase);
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
        var source = await db.BoardColumns.FindAsync(processor.ColumnId)
            ?? throw new InvalidOperationException($"La colonne source #{processor.ColumnId} n'existe plus.");
        // A waiting processor may legitimately ask to wake itself later even when its route table
        // only declares business outcomes. Keep an explicit scheduled route authoritative, but do
        // not send an otherwise valid scheduled result through the default failure destination.
        if (isScheduled && route is null && source.Role == ColumnRole.Waiting)
            targetId = source.Id;
        var target = await db.BoardColumns.FindAsync(targetId.Value)
            ?? throw new InvalidOperationException($"La colonne cible #{targetId} n'existe plus.");
        BoardColumn? wakeTarget = null;
        if (isScheduled)
        {
            if (result.FireAt is null || string.IsNullOrWhiteSpace(result.ScheduleTarget))
            {
                await FailAttemptAsync(projectSlug, execution, processor,
                    "L'issue 'scheduled' exige fireAt et scheduleTarget.", author);
                return;
            }
            if (target.Role != ColumnRole.Waiting)
            {
                await FailAttemptAsync(projectSlug, execution, processor,
                    "L'issue 'scheduled' doit être routée vers une colonne de rôle Attente.", author);
                return;
            }
            wakeTarget = await db.BoardColumns.FirstOrDefaultAsync(c =>
                c.PipelineId == target.PipelineId && c.Name == result.ScheduleTarget);
            if (wakeTarget is null)
            {
                await FailAttemptAsync(projectSlug, execution, processor,
                    $"La colonne de réveil '{result.ScheduleTarget}' n'existe pas dans le pipeline cible.", author);
                return;
            }
        }
        var ticket = await db.Tickets.FindAsync(execution.TicketId)
            ?? throw new InvalidOperationException($"Le ticket #{execution.TicketId} n’existe plus.");
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        if (row is null) return;
        var oldStatus = ticket.Status;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? successTransaction = null;
        if (target.Role == ColumnRole.Success)
        {
            var rejection = await ValidateSuccessContextAsync(db, row, ticket, result);
            if (rejection is not null)
            {
                row.ContextRejectionReason = rejection;
                row.Error = rejection;
                await db.SaveChangesAsync();
                await FailAttemptAsync(projectSlug, execution, processor, rejection, author);
                return;
            }
            row.ConsumedTicketUpdatedAt = ticket.UpdatedAt;
            row.ConsumedOwnerCommentId = result.Evidence?.OwnerFeedbackCommentId;

            if (BeforeSuccessCompareAndSwapAsync is not null)
                await BeforeSuccessCompareAndSwapAsync();

            successTransaction = await db.Database.BeginTransactionAsync();
            var expectedUpdatedAt = ticket.UpdatedAt;
            var completedAt = DateTime.UtcNow;
            ColumnAssignmentPolicy.Apply(ticket, source, target);
            var updated = await db.Tickets
                .Where(item => item.Id == ticket.Id && item.UpdatedAt == expectedUpdatedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.AssignedTo,
                        ticket.AssignedTo)
                    .SetProperty(item => item.PipelineId, target.PipelineId)
                    .SetProperty(item => item.ColumnId, target.Id)
                    .SetProperty(item => item.Status, target.Name)
                    .SetProperty(item => item.FireAt, isScheduled ? result.FireAt : null)
                    .SetProperty(item => item.ScheduleTarget, isScheduled ? wakeTarget!.Name : null)
                    .SetProperty(item => item.UpdatedAt, completedAt));
            if (updated == 0)
            {
                await successTransaction!.RollbackAsync();
                await successTransaction.DisposeAsync();
                successTransaction = null;
                row.ContextRejectionReason = "stale_ticket_context";
                row.Error = "stale_ticket_context";
                await db.SaveChangesAsync();
                await FailAttemptAsync(projectSlug, execution, processor,
                    "stale_ticket_context", author);
                return;
            }

            db.Entry(ticket).State = EntityState.Detached;
        }
        if (target.Role != ColumnRole.Success)
        {
            ColumnAssignmentPolicy.Apply(ticket, source, target);
            ticket.PipelineId = target.PipelineId;
            ticket.ColumnId = target.Id;
            ticket.Status = target.Name;
            ticket.FireAt = isScheduled ? result.FireAt : null;
            ticket.ScheduleTarget = isScheduled ? wakeTarget!.Name : null;
            ticket.UpdatedAt = DateTime.UtcNow;
        }
        row.Status = ColumnExecutionStatus.Completed;
        row.Outcome = result.Outcome;
        row.Summary = result.Summary;
        row.EndedAt = DateTime.UtcNow;
        row.TargetColumnId = target.Id;
        var comments = await db.Comments.AsNoTracking()
            .Where(comment => comment.TicketId == ticket.Id && comment.CreatedAt >= row.ClaimedAt)
            .OrderBy(comment => comment.Id)
            .Select(comment => new ProgressComment(comment.Author, comment.Content))
            .ToListAsync();
        row.AgentResult = result;
        var progress = BuildProgress(result, row.CompletedActionIds, comments);
        row.ProgressFingerprint = progress.Fingerprint;
        row.ProgressSignalsJson = JsonSerializer.Serialize(progress.Signals);
        db.ActivityEntries.Add(new ActivityEntry
        {
            TicketId = ticket.Id,
            Author = author,
            Text = $"traitement de colonne terminé ({result.Outcome}) : {oldStatus} → {target.Name}",
        });
        await db.SaveChangesAsync();
        if (successTransaction is not null)
        {
            await successTransaction.CommitAsync();
            await successTransaction.DisposeAsync();
        }
        tickets.NotifyStatusChanged(projectSlug, ticket.Id, oldStatus, target.Name);
    }

    public async Task<string?> ValidateSuccessContextAsync(
        string projectSlug, ColumnExecution execution, ColumnProcessor processor, ColumnAgentResult result)
    {
        var targetId = processor.Routes.FirstOrDefault(r =>
            string.Equals(r.Outcome, result.Outcome, StringComparison.OrdinalIgnoreCase))?.TargetColumnId
            ?? processor.DefaultTargetColumnId;
        if (targetId is null) return null;
        await using var db = projects.GetProjectDb(projectSlug);
        await ColumnService.EnsureBoardColumnsTableAsync(db);
        await EnsureTableAsync(db);
        var target = await db.BoardColumns.AsNoTracking().FirstOrDefaultAsync(column => column.Id == targetId);
        if (target?.Role != ColumnRole.Success) return null;
        var row = await db.ColumnExecutions.FindAsync(execution.Id);
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(item => item.Id == execution.TicketId);
        if (row is null) return "ticket_refresh_failed";
        if (ticket is null)
        {
            row.ContextRejectionReason = "ticket_refresh_failed";
            row.Error = "ticket_refresh_failed";
            await db.SaveChangesAsync();
            execution.ContextRejectionReason = row.ContextRejectionReason;
            return row.ContextRejectionReason;
        }
        var rejection = await ValidateSuccessContextAsync(db, row, ticket, result);
        if (rejection is null)
        {
            row.ConsumedTicketUpdatedAt = ticket.UpdatedAt;
            row.ConsumedOwnerCommentId = result.Evidence?.OwnerFeedbackCommentId;
        }
        else
        {
            row.ContextRejectionReason = rejection;
            row.Error = rejection;
        }
        await db.SaveChangesAsync();
        execution.ConsumedTicketUpdatedAt = row.ConsumedTicketUpdatedAt;
        execution.ConsumedOwnerCommentId = row.ConsumedOwnerCommentId;
        execution.ContextRejectionReason = row.ContextRejectionReason;
        return rejection;
    }

    private static async Task<string?> ValidateSuccessContextAsync(
        TodoDbContext db, ColumnExecution row, Ticket ticket, ColumnAgentResult result)
    {
        if (row.TriggerTicketUpdatedAt is null) return "ticket_refresh_failed";
        if (result.Evidence?.TicketUpdatedAt is DateTime consumedVersion
            && NormalizeUtcInstant(consumedVersion) != NormalizeUtcInstant(ticket.UpdatedAt))
            return "stale_ticket_context";
        if (row.TriggerOwnerCommentId is not int ownerCommentId)
            return result.Evidence?.TicketUpdatedAt is DateTime
                || ticket.UpdatedAt == row.TriggerTicketUpdatedAt
                    ? null
                    : "stale_ticket_context";
        if (result.Evidence?.OwnerFeedbackCommentId != ownerCommentId)
            return "owner_feedback_not_consumed";
        if (result.Evidence.DeliveryCommentId is not int deliveryCommentId)
            return "owner_feedback_not_consumed";
        var delivery = await db.Comments.AsNoTracking().FirstOrDefaultAsync(comment =>
            comment.Id == deliveryCommentId && comment.TicketId == ticket.Id && comment.Author != "owner");
        if (delivery is null || delivery.CreatedAt <= row.TriggerOwnerCommentCreatedAt
            || result.Evidence.DeliveryProducedAt is DateTime producedAt && producedAt < delivery.CreatedAt)
            return "owner_feedback_not_consumed";
        // A referenced delivery comment legitimately advances UpdatedAt. Any later mutation,
        // especially newer owner feedback, must still invalidate the result.
        if (Math.Abs((ticket.UpdatedAt - delivery.CreatedAt).TotalSeconds) >= 1)
            return "stale_ticket_context";
        var newerOwnerFeedback = await db.Comments.AsNoTracking().AnyAsync(comment =>
            comment.TicketId == ticket.Id && comment.Author == "owner" && comment.CreatedAt > delivery.CreatedAt);
        if (newerOwnerFeedback) return "stale_ticket_context";
        if (result.Evidence.Deliverables is not { Count: > 0 }
            || result.Evidence.Deliverables.Any(item =>
                string.IsNullOrWhiteSpace(item.Path)
                || string.IsNullOrWhiteSpace(item.Verification)
                || item.UpdatedAt <= row.TriggerOwnerCommentCreatedAt))
            return "stale_ticket_context";
        return null;
    }

    private static DateTime NormalizeUtcInstant(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // SQLite does not persist DateTimeKind. KittyClaw stores these timestamps as UTC,
        // so a materialized Unspecified value must not be interpreted as machine-local time.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static (string Fingerprint, string[] Signals) BuildProgress(
        ColumnAgentResult result, IReadOnlyCollection<string> completedActionIds,
        IReadOnlyCollection<ProgressComment> comments)
    {
        var signals = new List<string> { $"outcome:{NormalizeProgressText(result.Outcome)}" };
        var normalizedSummary = NormalizeProgressText(result.Summary);
        if (normalizedSummary.Length > 0) signals.Add($"summary:{normalizedSummary}");
        signals.AddRange(completedActionIds.Select(NormalizeProgressText)
            .Where(value => value.Length > 0)
            .Select(value => $"checkpoint:action:{value}"));
        signals.AddRange(comments.Where(IsRelevantProgressComment)
            .Select(comment => NormalizeProgressText(comment.Content))
            .Where(value => value.Length > 0)
            .Select(value => $"comment:{value}"));
        var canonical = string.Join("\n", signals.Distinct(StringComparer.Ordinal));
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(),
            signals.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string NormalizeProgressText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\b\d{4}-\d{2}-\d{2}[t ][0-9:.+\-z]+\b", "<timestamp>");
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}<>]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool IsRelevantProgressComment(ProgressComment comment)
    {
        var author = NormalizeProgressText(comment.Author);
        if (author.Length == 0) return false;
        return author is not "automation" and not "system" and not "kittyclaw";
    }

    private static bool HasMaterialProgressSignal(string? progressSignalsJson)
    {
        if (string.IsNullOrWhiteSpace(progressSignalsJson)) return false;
        try
        {
            var signals = JsonSerializer.Deserialize<string[]>(progressSignalsJson) ?? [];
            return signals.Any(signal => signal.StartsWith("checkpoint:", StringComparison.Ordinal)
                || signal.StartsWith("comment:", StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            // Historical or corrupt diagnostics must not disable the bounded-cycle safeguard.
            return false;
        }
    }

    private sealed record ProgressComment(string Author, string Content);

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
                var source = await db.BoardColumns.FindAsync(processor.ColumnId);
                ColumnAssignmentPolicy.Apply(ticket, source, target);
                ticket.PipelineId = target.PipelineId;
                ticket.ColumnId = target.Id;
                ticket.Status = target.Name;
                ticket.UpdatedAt = DateTime.UtcNow;
                // The failed execution is terminal once the ticket has been routed.
                // Keeping it in Failed would retain the active-ticket lock and prevent
                // the destination column processor from claiming the ticket.
                row.Status = ColumnExecutionStatus.Completed;
                row.Outcome = "technical_failure";
                row.TargetColumnId = target.Id;
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
            if (execution.AgentCompleted
                && execution.CapitalizationStatus is not (MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange))
                execution.CapitalizationStatus = MemoryCapitalizationStatus.RetryRequired;
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
        if (execution.AgentCompleted
            && execution.CapitalizationStatus is not (MemoryCapitalizationStatus.Succeeded or MemoryCapitalizationStatus.NoChange))
            execution.CapitalizationStatus = MemoryCapitalizationStatus.RetryRequired;
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
