using KittyClaw.Core.Data;
using KittyClaw.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Services;

public sealed partial class ColumnExecutionService
{
    private static Task EnsureQuestionsTableAsync(TodoDbContext db) =>
        MigrationGate.RunOnceAsync(db, "ticket-questions-v1", static d => d.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS TicketQuestions (
                Id TEXT NOT NULL PRIMARY KEY, TicketId INTEGER NOT NULL, ExecutionId TEXT NOT NULL,
                Question TEXT NOT NULL, Author TEXT NOT NULL, MultiSelect INTEGER NOT NULL,
                AllowFreeText INTEGER NOT NULL, CreatedAt TEXT NOT NULL, AnsweredAt TEXT NULL,
                FreeText TEXT NULL, Cancelled INTEGER NOT NULL DEFAULT 0,
                OptionsJson TEXT NOT NULL, SelectedOptionsJson TEXT NOT NULL DEFAULT '[]');
            CREATE INDEX IF NOT EXISTS IX_TicketQuestions_Ticket ON TicketQuestions(TicketId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TicketQuestions_Pending ON TicketQuestions(ExecutionId)
                WHERE AnsweredAt IS NULL AND Cancelled = 0;
            """));

    public async Task<List<TicketQuestion>> ListQuestionsAsync(string slug, int ticketId)
    {
        await using var db = projects.GetProjectDb(slug);
        await EnsureTableAsync(db);
        return await db.TicketQuestions.AsNoTracking().Where(q => q.TicketId == ticketId)
            .OrderBy(q => q.CreatedAt).ToListAsync();
    }

    public async Task<TicketQuestion> AskQuestionAsync(string slug, int ticketId, AskTicketQuestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 8000 ||
            string.IsNullOrWhiteSpace(request.Author) || request.Author.Length > 200 ||
            request.Options is null || request.Options.Count is < 2 or > 12 ||
            request.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 1000) ||
            request.Options.Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Options.Count)
            throw new ArgumentException("Supply a question, an author and 2–12 distinct non-empty options.");
        // Run the ticket schema migrations before querying the entity.
        if (await tickets.GetTicketAsync(slug, ticketId) is null) throw new KeyNotFoundException("Ticket not found.");
        await using var db = projects.GetProjectDb(slug);
        await EnsureTableAsync(db);
        await ColumnProcessorService.EnsureTableAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId);
        var execution = await db.ColumnExecutions.FirstOrDefaultAsync(e => e.TicketId == ticketId &&
            (request.ExecutionId == null || e.Id == request.ExecutionId) &&
            (e.Status == ColumnExecutionStatus.Running || e.Status == ColumnExecutionStatus.Retrying ||
             e.Status == ColumnExecutionStatus.Failed || e.Status == ColumnExecutionStatus.WaitingForAnswer));
        var processor = await db.ColumnProcessors.FirstOrDefaultAsync(p => p.ColumnId == ticket.ColumnId && p.Enabled);
        if (processor is null || execution is not null &&
            (execution.ProcessorId != processor.Id || execution.ContextRejectionReason == "manual_transition"))
            throw new InvalidOperationException("The ticket needs an enabled processor in its current column to resume.");
        if (execution is null)
        {
            if (request.ExecutionId is not null) throw new InvalidOperationException("The execution is no longer active for this ticket.");
            if (await db.ColumnExecutions.AnyAsync(e => e.TicketId == ticketId && e.Status == ColumnExecutionStatus.WaitingForChildren))
                throw new InvalidOperationException("The ticket is waiting for child tickets.");
            execution = new ColumnExecution { Id = Guid.NewGuid().ToString("N"), TicketId = ticketId,
                ProcessorId = processor.Id, TriggerTicketUpdatedAt = ticket.UpdatedAt };
            db.ColumnExecutions.Add(execution);
        }
        var pending = await db.TicketQuestions.FirstOrDefaultAsync(q => q.ExecutionId == execution.Id && q.AnsweredAt == null && !q.Cancelled);
        if (pending is not null)
        {
            if (pending.Question == request.Question.Trim() && pending.Options.SequenceEqual(request.Options.Select(o => o.Trim())) &&
                pending.MultiSelect == request.MultiSelect && pending.AllowFreeText == request.AllowFreeText) return pending;
            throw new InvalidOperationException("Answer the existing question before asking another.");
        }
        var question = new TicketQuestion { Id = Guid.NewGuid().ToString("N"), TicketId = ticketId,
            ExecutionId = execution.Id, Question = request.Question.Trim(), Author = request.Author.Trim(),
            Options = request.Options.Select(o => o.Trim()).ToList(), MultiSelect = request.MultiSelect,
            AllowFreeText = request.AllowFreeText };
        db.TicketQuestions.Add(question);
        execution.Status = ColumnExecutionStatus.WaitingForAnswer;
        execution.AvailableAt = null;
        execution.Error = null;
        execution.EndedAt = null;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        ExecutionChanged?.Invoke(slug, ticketId);
        return question;
    }

    public async Task<TicketQuestion> AnswerQuestionAsync(string slug, int ticketId, string questionId, AnswerTicketQuestionRequest request)
    {
        await using var db = projects.GetProjectDb(slug);
        await EnsureTableAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var question = await db.TicketQuestions.SingleOrDefaultAsync(q => q.Id == questionId && q.TicketId == ticketId)
            ?? throw new KeyNotFoundException("Question not found.");
        if (question.AnsweredAt is not null || question.Cancelled) throw new InvalidOperationException("This question is already closed.");
        var selected = request.SelectedOptions ?? [];
        var freeText = request.FreeText?.Trim();
        if (selected.Count != selected.Distinct().Count() || selected.Any(o => !question.Options.Contains(o)) ||
            !question.MultiSelect && selected.Count > 1 || !question.AllowFreeText && !string.IsNullOrWhiteSpace(freeText) ||
            selected.Count == 0 && string.IsNullOrWhiteSpace(freeText) || freeText?.Length > 8000)
            throw new ArgumentException("Choose valid options or provide an allowed free-text answer.");
        var execution = await db.ColumnExecutions.SingleAsync(e => e.Id == question.ExecutionId);
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId);
        var processor = await db.ColumnProcessors.SingleOrDefaultAsync(p => p.Id == execution.ProcessorId);
        if (ticket is null || processor is null || !processor.Enabled || ticket.ColumnId != processor.ColumnId ||
            execution.Status != ColumnExecutionStatus.WaitingForAnswer || execution.ContextRejectionReason == "manual_transition")
            throw new InvalidOperationException("The ticket moved or its execution was cancelled. It cannot be resumed from this question.");
        var now = DateTime.UtcNow;
        question.SelectedOptions = selected;
        question.FreeText = freeText;
        question.AnsweredAt = now;
        var comment = new Comment { TicketId = ticketId, Author = "owner", CreatedAt = now,
            Content = $"Agent question ({question.Author}): {question.Question}\n\nAnswer: {string.Join(", ", selected)}\n{freeText}".Trim() };
        db.Comments.Add(comment);
        ticket.UpdatedAt = now;
        execution.Status = ColumnExecutionStatus.Retrying;
        execution.AvailableAt = now;
        execution.EndedAt = null;
        execution.AgentCompleted = false;
        execution.AgentResult = null;
        execution.Outcome = null;
        execution.Summary = null;
        execution.Error = null;
        execution.TriggerTicketUpdatedAt = now;
        execution.TriggerSignalType = "owner_answer";
        // Waiting for a human is not a failed attempt.
        execution.Attempt = Math.Max(0, execution.Attempt - 1);
        execution.CapitalizationStatus = MemoryCapitalizationStatus.Pending;
        execution.CapitalizationError = null;
        execution.CapitalizedAt = null;
        await db.SaveChangesAsync();
        execution.TriggerOwnerCommentId = comment.Id;
        execution.TriggerOwnerCommentCreatedAt = now;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        ExecutionChanged?.Invoke(slug, ticketId);
        return question;
    }
}
