using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

/// <summary>Human-only acceptance and profile approvals, with immutable snapshots and atomic workflow effects.</summary>
public sealed class JevWorkDecisionService(ProjectService projects, TicketService tickets,
    ColumnProcessorService processors, JevWorkReviewService reviews)
{
    public async Task<JevWorkDecision> DecideAsync(string slug, int ticketId, int columnId, string reviewId, bool accepted, string? reason = null)
    {
        reason = reason?.Trim();
        if (reason?.Length > 2000) throw new InvalidOperationException("A decision reason is limited to 2,000 characters.");
        if (!accepted && string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Explain why this review should be rejected.");
        try
        {
            var existing = (await reviews.DecisionsAsync(slug, ticketId)).FirstOrDefault(d => d.ReviewId == reviewId);
            if (existing is not null) return Same(existing, accepted);
            var review = await reviews.GetAsync(slug, ticketId, columnId);
            var ticket = await tickets.GetTicketAsync(slug, ticketId) ?? throw new InvalidOperationException("The ticket no longer exists.");
            var processor = await processors.GetAsync(slug, columnId) ?? throw new InvalidOperationException("The column processor no longer exists.");
            if (review is null || review.Id != reviewId || review.Error is not null || !await reviews.IsCurrentAsync(slug, processor, ticket, review))
                throw new InvalidOperationException("The review is missing, incomplete or out of date. Wait for a new review.");
            if ((await projects.GetProjectAsync(slug)) is not { IsPaused: false }) throw new InvalidOperationException("Resume this project before applying a decision.");
            int? target = null;
            string? correction = null;
            if (review.Kind == "acceptance")
            {
                if (accepted && review.Criteria.Any(c => c.Status != "satisfied") && string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("Explain the owner override for missing, unverified or unmet criteria.");
                target = accepted ? processor.DefaultTargetColumnId : processor.Routes.FirstOrDefault(r => r.Outcome.Equals("changes_requested", StringComparison.OrdinalIgnoreCase))?.TargetColumnId;
                if (accepted && target is null) throw new InvalidOperationException("Configure the validation column's default destination before approving.");
                if (!accepted) correction = "Acceptance review: changes requested.\n" + reason + "\n" +
                    string.Join("\n", review.Criteria.Where(c => c.Status != "satisfied").Select(c => $"- {c.Criterion}: {c.Status}"));
            }
            else if (accepted && review.Profile is null) throw new InvalidOperationException("JEV did not select an execution profile. Update the ticket or authorized profiles.");

            var decision = new JevWorkDecision(reviewId, ticketId, columnId, review.Kind, accepted, DateTime.UtcNow, reason, target, review);
            async Task PersistAsync(KittyClaw.Core.Data.TodoDbContext db)
            {
                if ((await JevWorkReviewService.ReadAsync(db, ticketId, columnId))?.Id != reviewId)
                    throw new InvalidOperationException("The review changed while applying the decision.");
                var current = await db.ColumnProcessors.AsNoTracking().SingleOrDefaultAsync(p => p.ColumnId == columnId);
                if (current is null || !await reviews.IsCurrentAsync(slug, current, ticket, review, db))
                    throw new InvalidOperationException("The ticket, evidence or column configuration changed while applying the decision.");
                await ColumnExecutionService.EnsureTableAsync(db);
                if (await db.ColumnExecutions.AnyAsync(e => e.TicketId == ticketId && (e.Status == ColumnExecutionStatus.Running || e.Status == ColumnExecutionStatus.Retrying || e.Status == ColumnExecutionStatus.WaitingForChildren)))
                    throw new InvalidOperationException("An execution is already active for this ticket.");
                await JevWorkReviewService.SaveDecisionAsync(db, decision);
            }
            if (target is int targetId)
            {
                await tickets.ApplyJevProposalAsync(slug, ticketId, targetId, ticket.UpdatedAt,
                    async (db, _) => await PersistAsync(db), comment: correction);
            }
            else
            {
                await using var db = projects.GetProjectDb(slug); await JevWorkReviewService.PrepareAsync(db);
                await ColumnExecutionService.EnsureTableAsync(db);
                await using var transaction = await db.Database.BeginTransactionAsync();
                // Obtain the writer lock and compare the ticket snapshot before any decision is committed.
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Tickets SET UpdatedAt = UpdatedAt WHERE Id = {ticketId} AND UpdatedAt = {ticket.UpdatedAt} AND ColumnId = {columnId}") != 1)
                    throw new InvalidOperationException("The ticket changed while applying the decision.");
                await PersistAsync(db); await db.SaveChangesAsync(); await transaction.CommitAsync();
            }
            return decision;
        }
        catch (Exception)
        {
            var winner = (await reviews.DecisionsAsync(slug, ticketId)).FirstOrDefault(d => d.ReviewId == reviewId);
            if (winner is not null) return Same(winner, accepted);
            throw;
        }
    }

    private static JevWorkDecision Same(JevWorkDecision decision, bool accepted) => decision.Accepted == accepted
        ? decision : throw new InvalidOperationException("This review already has a different decision. Obtain a new review.");
}
