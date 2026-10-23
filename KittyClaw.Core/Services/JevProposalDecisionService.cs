using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Apply human decisions or explicitly enabled confidence-gated routing through normal transition rules.</summary>
public sealed class JevProposalDecisionService(ProjectService projects, TicketService tickets,
    ColumnProcessorService processors, JevDecisionStore decisions)
{
    public Task<JevProposalDecision> DecideAsync(string slug, int ticketId, string proposalId,
        bool accepted, string? reason = null) => DecideCoreAsync(slug, ticketId, proposalId, accepted, reason, false);

    public async Task<JevProposalDecision?> TryApplyAutomaticallyAsync(string slug, int ticketId, string proposalId)
    {
        // Expected ineligibility leaves the proposal available for manual review.
        if ((await decisions.ListAsync(slug, ticketId)).Any(d => d.ProposalId == proposalId)) return null;
        try { return await DecideCoreAsync(slug, ticketId, proposalId, true, null, true); }
        catch (InvalidOperationException) { return null; }
    }

    private async Task<JevProposalDecision> DecideCoreAsync(string slug, int ticketId, string proposalId,
        bool accepted, string? reason, bool automatic)
    {
        reason = reason?.Trim();
        if (reason?.Length > 2000) throw new InvalidOperationException("The decision reason is limited to 2,000 characters.");
        if (!accepted && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Explain why the proposal should be rejected.");
        try
        {
            var existing = (await decisions.ListAsync(slug, ticketId)).FirstOrDefault(d => d.ProposalId == proposalId);
            if (existing is not null) return SameDecision(existing, accepted);
            await using var db = projects.GetProjectDb(slug);
            await JevDecisionStore.PrepareAsync(db);
            if (automatic) await ColumnExecutionService.EnsureTableAsync(db);
            var proposal = await JevDecisionStore.FindCurrentAsync(db, ticketId, proposalId)
                ?? throw new InvalidOperationException("The proposal has changed. Reload the ticket.");
            if (proposal.Error is not null || proposal.Choice is null || !proposal.Destinations.ContainsKey(proposal.Choice))
                throw new InvalidOperationException("An incomplete proposal cannot be accepted or rejected.");
            var ticket = await tickets.GetTicketAsync(slug, ticketId)
                ?? throw new InvalidOperationException("The ticket no longer exists.");
            int? targetId = null;
            double? threshold = null;
            if (accepted)
            {
                if (!proposal.Choice.StartsWith("column-", StringComparison.Ordinal) || !int.TryParse(proposal.Choice[7..], out var target))
                    throw new InvalidOperationException("This proposal has no destination to apply.");
                var columns = await db.BoardColumns.AsNoTracking().ToListAsync();
                var configuredSource = await processors.GetAsync(slug, proposal.SourceColumnId);
                var sourceColumn = columns.SingleOrDefault(c => c.Id == proposal.SourceColumnId)
                    ?? throw new InvalidOperationException("The source column no longer exists.");
                var source = proposal.ExecutionId is null ? JevQualificationService.RecommendationProcessor(configuredSource, sourceColumn, columns)
                    : configuredSource ?? throw new InvalidOperationException("The source processor no longer exists.");
                if (!JevQualificationService.BuildDestinations(source, columns).ContainsKey(proposal.Choice))
                    throw new InvalidOperationException("The proposed route is no longer configured.");
                if (!source.Enabled || proposal.TicketUpdatedAt != ticket.UpdatedAt || proposal.TicketColumnId != ticket.ColumnId)
                    throw new InvalidOperationException("The ticket or workflow changed. Obtain a new proposal before accepting it.");
                if (proposal.ExecutionId is null)
                {
                    var feedback = JevQualificationService.RejectionFeedback(await decisions.ListAsync(slug, ticketId), source, ticket);
                    if (source.JevAcceptanceValidation || !source.HasBusinessRouting || proposal.Fingerprint != JevQualificationService.Fingerprint(source, ticket, columns, feedback.Count == 0 ? null : feedback, proposal.RoutingResult))
                        throw new InvalidOperationException("The qualification context changed. Obtain a new proposal before accepting it.");
                }
                else
                {
                    var execution = await db.ColumnExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == proposal.ExecutionId);
                    if (!source.JevPostRunObservation || execution?.EndedAt != proposal.ExecutionEndedAt || execution?.ProcessorId != source.Id || execution?.RunId != proposal.RunId)
                        throw new InvalidOperationException("The agent execution changed. Obtain a new proposal before accepting it.");
                }
                if (automatic) threshold = await EnsureAutomaticPolicyAsync(slug, db, proposal, ticketId);
                targetId = target;
            }
            var decision = new JevProposalDecision(proposalId, ticketId, accepted, DateTime.UtcNow, automatic ? "JEV" : "owner", reason, proposal, targetId, automatic, threshold);
            if (accepted && targetId != ticket.ColumnId)
            {
                var updated = await tickets.ApplyJevProposalAsync(slug, ticketId, targetId!.Value, ticket.UpdatedAt, async (transactionDb, _) =>
                {
                    if (await JevDecisionStore.FindCurrentAsync(transactionDb, ticketId, proposalId) is null)
                        throw new InvalidOperationException("The proposal changed while accepting it. Reload the ticket.");
                    await EnsureExecutionIsCurrentAsync(transactionDb, proposal);
                    if (automatic) await EnsureAutomaticPolicyAsync(slug, transactionDb, proposal, ticketId, threshold);
                    await JevDecisionStore.SaveAsync(transactionDb, decision);
                }, author: decision.Actor);
                if (updated is null) throw new InvalidOperationException("The ticket no longer exists.");
            }
            else
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && (!accepted || t.UpdatedAt == ticket.UpdatedAt && t.ColumnId == ticket.ColumnId)) || await JevDecisionStore.FindCurrentAsync(db, ticketId, proposalId) is null)
                    throw new InvalidOperationException("The ticket or proposal changed. Reload the ticket.");
                if (accepted) await EnsureExecutionIsCurrentAsync(db, proposal);
                if (automatic) await EnsureAutomaticPolicyAsync(slug, db, proposal, ticketId, threshold);
                await JevDecisionStore.SaveAsync(db, decision);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            return decision;
        }
        catch (Exception)
        {
            // A repeated click or another tab may already have committed the same decision.
            var winner = (await decisions.ListAsync(slug, ticketId)).FirstOrDefault(d => d.ProposalId == proposalId);
            if (winner is not null) return SameDecision(winner, accepted);
            throw;
        }
    }

    private async Task<double> EnsureAutomaticPolicyAsync(string slug, KittyClaw.Core.Data.TodoDbContext db,
        JevRoutingProposal proposal, int proposalTicketId, double? expectedThreshold = null)
    {
        var source = await db.ColumnProcessors.AsNoTracking().SingleOrDefaultAsync(p => p.ColumnId == proposal.SourceColumnId);
        if (source is null || !source.UsesAutomaticJevRouting ||
            (proposal.ExecutionId is not null && !source.JevPostRunObservation) ||
            !double.IsFinite(source.JevConfidenceThreshold) || source.JevConfidenceThreshold < 0 || source.JevConfidenceThreshold > 1 ||
            proposal.Confidence is not double confidence || !double.IsFinite(confidence) || confidence < source.JevConfidenceThreshold ||
            (expectedThreshold.HasValue && expectedThreshold != source.JevConfidenceThreshold) ||
            (await projects.GetProjectAsync(slug)) is not { IsPaused: false })
            throw new InvalidOperationException("Automatic routing is disabled or this proposal does not meet the current confidence threshold.");
        var columns = await db.BoardColumns.AsNoTracking().ToListAsync();
        if (proposal.ProcessorFingerprint is null || proposal.ProcessorFingerprint != JevQualificationService.RoutingConfigurationFingerprint(source, columns))
            throw new InvalidOperationException("The routing context changed. Obtain a new proposal before automatic routing.");
        if (await db.ColumnExecutions.AnyAsync(e => e.TicketId == proposalTicketId &&
            (e.Status == ColumnExecutionStatus.Running || e.Status == ColumnExecutionStatus.Retrying || e.Status == ColumnExecutionStatus.WaitingForChildren)))
            throw new InvalidOperationException("An active execution requires manual review before changing the route.");
        if (await JevDecisionStore.HasRecentAutomaticTransitionAsync(db, proposalTicketId, proposal.SourceColumnId, proposal.Choice!))
            throw new InvalidOperationException("This automatic transition recently occurred. Manual review is required to repeat it.");
        return source.JevConfidenceThreshold;
    }

    private static async Task EnsureExecutionIsCurrentAsync(KittyClaw.Core.Data.TodoDbContext db, JevRoutingProposal proposal)
    {
        if (proposal.ExecutionId is not null && !await db.ColumnExecutions.AnyAsync(e => e.Id == proposal.ExecutionId &&
            e.EndedAt == proposal.ExecutionEndedAt && e.RunId == proposal.RunId &&
            (e.Status == ColumnExecutionStatus.Completed || e.Status == ColumnExecutionStatus.Failed || e.Status == ColumnExecutionStatus.Cancelled)))
            throw new InvalidOperationException("The agent execution changed. Obtain a new proposal before accepting it.");
    }

    private static JevProposalDecision SameDecision(JevProposalDecision existing, bool accepted) =>
        existing.Accepted == accepted ? existing : throw new InvalidOperationException("This proposal already has a different decision. Review a new proposal instead.");
}
