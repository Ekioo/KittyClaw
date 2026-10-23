using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public class TicketImprovementTests
{
    [Fact]
    public void Parse_AcceptsFencedJsonAndRejectsIncompleteProposal()
    {
        var proposal = KittyClaw.Web.Services.TicketImprovementService.Parse("```json\n{\"description\":\"Clear scope\",\"acceptanceCriteria\":[\"Observable result\"]}\n```");
        Assert.Equal("Clear scope", proposal.Description);
        Assert.Single(proposal.AcceptanceCriteria);
        Assert.Throws<InvalidOperationException>(() => KittyClaw.Web.Services.TicketImprovementService.Parse("{\"description\":\"Scope\",\"acceptanceCriteria\":[]}"));
    }

    [Fact]
    public async Task Apply_PreservesUnchangedEvidenceAndRejectsStaleProposal()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("improvement");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Search", "Original");
        var criterion = await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null,
            new("Existing criterion", AcceptanceStatus.PASS, new("Verified")));
        var before = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        await tickets.ApplyImprovementAsync(project.Slug, ticket.Id, before.Description, before.AcceptanceCriteriaJson,
            "Improved", ["Existing criterion", "New criterion"]);
        var after = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal("Improved", after.Description);
        Assert.Equal(criterion, after.AcceptanceCriteria[0]);
        Assert.Equal(AcceptanceStatus.NotEvaluated, after.AcceptanceCriteria[1].Status);
        Assert.Null(after.AcceptanceCriteria[1].Evidence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.ApplyImprovementAsync(project.Slug,
            ticket.Id, before.Description, before.AcceptanceCriteriaJson, "Stale", ["Wrong"]));
        Assert.Equal("Improved", (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.Description);
    }

    [Fact]
    public async Task InvalidCriteria_DoNotChangeDescription()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("invalid-improvement");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Title", "Original");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.ApplyImprovementAsync(project.Slug,
            ticket.Id, ticket.Description, ticket.AcceptanceCriteriaJson, "Changed", [" "]));
        Assert.Equal("Original", (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.Description);
    }
}
