using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Only explicit human acceptance may apply a JEV proposal, using normal ticket transition rules.</summary>
public sealed class JevProposalDecisionService(ProjectService projects, TicketService tickets,
    ColumnProcessorService processors, JevDecisionStore decisions)
{
    public async Task<JevProposalDecision> DecideAsync(string slug, int ticketId, string proposalId,
        bool accepted, string? reason = null)
    {
        reason = reason?.Trim();
        if (reason?.Length > 2000) throw new InvalidOperationException("The decision reason is limited to 2,000 characters.");
        if (!accepted && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Explain why the proposal should be rejected.");
        var existing = (await decisions.ListAsync(slug, ticketId)).FirstOrDefault(d => d.ProposalId == proposalId);
        if (existing is not null) return SameDecision(existing, accepted);
        await using var db = projects.GetProjectDb(slug);
        await JevDecisionStore.PrepareAsync(db);
        var proposal = await JevDecisionStore.FindCurrentAsync(db, ticketId, proposalId)
            ?? throw new InvalidOperationException("The proposal has changed. Reload the ticket.");
        if (proposal.Error is not null || proposal.Choice is null || !proposal.Destinations.ContainsKey(proposal.Choice))
            throw new InvalidOperationException("An incomplete proposal cannot be accepted or rejected.");
        var ticket = await tickets.GetTicketAsync(slug, ticketId)
            ?? throw new InvalidOperationException("The ticket no longer exists.");
        int? targetId = null;
        if (accepted)
        {
            if (!proposal.Choice.StartsWith("column-", StringComparison.Ordinal) || !int.TryParse(proposal.Choice[7..], out var target))
                throw new InvalidOperationException("This proposal has no destination to apply.");
            var source = await processors.GetAsync(slug, proposal.SourceColumnId)
                ?? throw new InvalidOperationException("The source processor no longer exists.");
            var columns = await db.BoardColumns.AsNoTracking().ToListAsync();
            if (!JevQualificationService.BuildDestinations(source, columns).ContainsKey(proposal.Choice))
                throw new InvalidOperationException("The proposed route is no longer configured.");
            if (!source.Enabled || proposal.TicketUpdatedAt != ticket.UpdatedAt || proposal.TicketColumnId != ticket.ColumnId)
                throw new InvalidOperationException("The ticket or workflow changed. Obtain a new proposal before accepting it.");
            if (proposal.ExecutionId is null)
            {
                var feedback = JevQualificationService.RejectionFeedback(await decisions.ListAsync(slug, ticketId), source, ticket);
                if (!source.JevObservationOnly || proposal.Fingerprint != JevQualificationService.Fingerprint(source, ticket, columns, feedback.Count == 0 ? null : feedback))
                    throw new InvalidOperationException("The qualification context changed. Obtain a new proposal before accepting it.");
            }
            else
            {
                var execution = await db.ColumnExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == proposal.ExecutionId);
                if (!source.JevPostRunObservation || execution?.EndedAt != proposal.ExecutionEndedAt || execution?.ProcessorId != source.Id || execution?.RunId != proposal.RunId)
                    throw new InvalidOperationException("The agent execution changed. Obtain a new proposal before accepting it.");
            }
            targetId = target;
        }
        var decision = new JevProposalDecision(proposalId, ticketId, accepted, DateTime.UtcNow, "owner", reason, proposal, targetId);
        try
        {
            if (accepted && targetId != ticket.ColumnId)
            {
                var updated = await tickets.ApplyJevProposalAsync(slug, ticketId, targetId!.Value, ticket.UpdatedAt, async (transactionDb, _) =>
                {
                    if (await JevDecisionStore.FindCurrentAsync(transactionDb, ticketId, proposalId) is null)
                        throw new InvalidOperationException("The proposal changed while accepting it. Reload the ticket.");
                    await EnsureExecutionIsCurrentAsync(transactionDb, proposal);
                    await JevDecisionStore.SaveAsync(transactionDb, decision);
                });
                if (updated is null) throw new InvalidOperationException("The ticket no longer exists.");
            }
            else
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && (!accepted || t.UpdatedAt == ticket.UpdatedAt && t.ColumnId == ticket.ColumnId)) || await JevDecisionStore.FindCurrentAsync(db, ticketId, proposalId) is null)
                    throw new InvalidOperationException("The ticket or proposal changed. Reload the ticket.");
                if (accepted) await EnsureExecutionIsCurrentAsync(db, proposal);
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
