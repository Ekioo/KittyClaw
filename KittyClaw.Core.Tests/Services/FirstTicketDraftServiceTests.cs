using System.Text.Json;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class FirstTicketDraftServiceTests
{
    [Fact]
    public async Task DraftEditAndConfirm_CreateCompleteTicketWithCorrelatedEvents()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("first-ticket");
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new FirstTicketDraftService(tmp.Path, tickets);
        var intake = new RepositoryIntakeState("journey-42", Path.Combine(tmp.Path, "repo"), "Ship a useful search", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, null);

        var draft = await service.DraftAsync(intake);
        await service.MarkEditedAsync(draft.JourneyId);
        var created = await service.ConfirmAsync(project.Slug, draft with { Context = "Search is currently unavailable." });

        var persisted = await tickets.GetTicketAsync(project.Slug, created.Id);
        Assert.NotNull(persisted);
        Assert.Equal("Ship a useful search", persisted.Title);
        Assert.Contains("Search is currently unavailable.", persisted.Description);
        Assert.Contains("Journey: `journey-42`", persisted.Description);
        Assert.Contains("Initial objective: Ship a useful search", persisted.Description);
        var events = ReadEvents(tmp.Path);
        Assert.Equal(["first_ticket_drafted", "first_ticket_edited", "first_ticket_confirmed"], events.Select(e => e.Name));
        Assert.All(events, e => Assert.Equal("journey-42", e.JourneyId));
        Assert.Equal(created.Id, events[^1].TicketId);
    }

    [Fact]
    public async Task InvalidIntakeOrEmptyConfirmation_IsRejected()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var tickets = new TicketService(projects, new MemberService(projects));
        var service = new FirstTicketDraftService(tmp.Path, tickets);
        var invalid = new RepositoryIntakeState("journey-bad", tmp.Path, "Goal", DateTimeOffset.UtcNow, null, false, "failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DraftAsync(invalid));
        var project = await projects.CreateProjectAsync("empty-ticket");
        var empty = new FirstTicketDraftModel("journey-bad", " ", "Context", " ", "Criteria", "Goal");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(project.Slug, empty));
    }

    private static List<FirstTicketEvent> ReadEvents(string root) =>
        File.ReadAllLines(Path.Combine(root, "activation", "first-ticket-events.jsonl"))
            .Select(line => JsonSerializer.Deserialize<FirstTicketEvent>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
}
