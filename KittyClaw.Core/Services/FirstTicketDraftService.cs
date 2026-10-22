using System.Text.Json;
using KittyClaw.Core.Models;

namespace KittyClaw.Core.Services;

public sealed class FirstTicketDraftService
{
    private readonly string _stateDirectory;
    private readonly TicketService _tickets;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FirstTicketDraftService(string dataDirectory, TicketService tickets)
    {
        _stateDirectory = Path.Combine(dataDirectory, "activation");
        _tickets = tickets;
    }

    public async Task<FirstTicketDraftModel> DraftAsync(RepositoryIntakeState intake, CancellationToken ct = default)
    {
        if (!intake.IsValidated)
            throw new InvalidOperationException("The repository must be validated before drafting the first ticket.");

        var objective = intake.Objective.Trim();
        if (objective.Length == 0)
            throw new InvalidOperationException("The first objective is required.");

        var title = objective.TrimEnd('.', '!', '?');
        if (title.Length > 90) title = title[..87].TrimEnd() + "…";
        var repositoryName = Path.GetFileName(intake.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var draft = new FirstTicketDraftModel(
            intake.JourneyId,
            title,
            $"Work in the validated repository `{repositoryName}` to address the owner's initial objective.",
            objective,
            $"- The objective is implemented in `{repositoryName}`.\n- Relevant automated checks pass.\n- The result is ready for human review.",
            objective);
        await AppendEventAsync(new FirstTicketEvent(intake.JourneyId, "first_ticket_drafted", DateTimeOffset.UtcNow), ct);
        return draft;
    }

    public Task MarkEditedAsync(string journeyId, CancellationToken ct = default) =>
        AppendEventAsync(new FirstTicketEvent(journeyId, "first_ticket_edited", DateTimeOffset.UtcNow), ct);

    public async Task<Ticket> ConfirmAsync(string projectSlug, FirstTicketDraftModel draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || string.IsNullOrWhiteSpace(draft.ExpectedOutcome))
            throw new InvalidOperationException("A title and expected outcome are required.");

        var description = $"""
            ## Context

            {draft.Context.Trim()}

            ## Expected outcome

            {draft.ExpectedOutcome.Trim()}

            ## Acceptance criteria

            {draft.AcceptanceCriteria.Trim()}

            ## Provenance

            Journey: `{draft.JourneyId}`

            Initial objective: {draft.InitialObjective.Trim()}
            """;
        var ticket = await _tickets.CreateTicketAsync(
            projectSlug, draft.Title.Trim(), description, createdBy: "owner",
            status: "Backlog", priority: TicketPriority.Required);
        await AppendEventAsync(new FirstTicketEvent(draft.JourneyId, "first_ticket_confirmed", DateTimeOffset.UtcNow, ticket.Id, projectSlug), ct);
        return ticket;
    }

    private async Task AppendEventAsync(FirstTicketEvent activationEvent, CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDirectory);
        await _writeLock.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(_stateDirectory, "first-ticket-events.jsonl"),
                JsonSerializer.Serialize(activationEvent, JsonOptions) + Environment.NewLine, ct);
        }
        finally { _writeLock.Release(); }
    }
}

public sealed record FirstTicketDraftModel(
    string JourneyId,
    string Title,
    string Context,
    string ExpectedOutcome,
    string AcceptanceCriteria,
    string InitialObjective);

public sealed record FirstTicketEvent(string JourneyId, string Name, DateTimeOffset OccurredAt,
    int? TicketId = null, string? ProjectSlug = null);
