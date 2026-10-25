using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace KittyClaw.Core.Tests.Services;

public class TicketImprovementTests
{
    [Fact]
    public void Parse_KeepsDepthAnalysisSeparateFromTheRewrittenTicket()
    {
        var expected = new KittyClaw.Web.Services.TicketImprovement(
            "Reproduce the failure and address its cause", ["The reproduction passes after the fix"],
            [], "1. Investigate the cause\n2. Raw logs\n3. Reproduction harness\n4. Regression result");

        var actual = KittyClaw.Web.Services.TicketImprovementService.Parse(
            System.Text.Json.JsonSerializer.Serialize(expected));

        Assert.Equal(expected.Analysis, actual.Analysis);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.AcceptanceCriteria, actual.AcceptanceCriteria);
        Assert.Empty(actual.SuggestedTickets!);
        Assert.Throws<InvalidOperationException>(() => KittyClaw.Web.Services.TicketImprovementService.Parse(
            System.Text.Json.JsonSerializer.Serialize(expected with { Analysis = new string('x', 10001) })));
    }

    [Fact]
    public void Parse_ValidatesStructuredBreakdown()
    {
        var draft = new TicketImprovementDraft("Small task", "Concrete scope", ["Observable result"]);
        string Json(List<TicketImprovementDraft> drafts) => System.Text.Json.JsonSerializer.Serialize(
            new KittyClaw.Web.Services.TicketImprovement("Overall scope", ["Overall result"], drafts));
        var proposal = KittyClaw.Web.Services.TicketImprovementService.Parse(Json([draft, draft with { Title = "Second task" }]));
        Assert.Equal(2, proposal.SuggestedTickets!.Count);
        Assert.Throws<InvalidOperationException>(() => KittyClaw.Web.Services.TicketImprovementService.Parse(Json([draft])));
        Assert.Throws<InvalidOperationException>(() => KittyClaw.Web.Services.TicketImprovementService.Parse(
            Json([draft, draft with { AcceptanceCriteria = [" "] }])));
    }

    [Fact]
    public async Task Split_CreatesLinkedTicketsWithCriteriaWithoutRewritingParent_AndRejectsReplay()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("split");
        var tickets = new TicketService(projects, new MemberService(projects));
        var original = await tickets.CreateTicketAsync(project.Slug, "Broad scope", "Keep this description");
        List<TicketImprovementDraft> drafts = [new("First", "First scope", ["First result"]), new("Second", "Second scope", ["Second result"])];
        var created = new List<int>();
        tickets.TicketCreated += (_, id) => created.Add(id);
        await tickets.SplitTicketAsync(project.Slug, original, drafts);
        var parent = (await tickets.GetTicketAsync(project.Slug, original.Id))!;
        Assert.Equal(original.Description, parent.Description);
        Assert.Equal(original.AcceptanceCriteriaJson, parent.AcceptanceCriteriaJson);
        Assert.Equal(2, created.Count);
        Assert.Equal(2, parent.SubTickets.Count);
        foreach (var id in created)
        {
            var child = (await tickets.GetTicketAsync(project.Slug, id))!;
            Assert.Equal(original.Id, child.ParentId);
            Assert.Equal(original.ColumnId, child.ColumnId);
            Assert.Equal(original.PipelineId, child.PipelineId);
            Assert.Single(child.AcceptanceCriteria);
            Assert.Equal(AcceptanceStatus.Pending, child.AcceptanceCriteria[0].Status);
        }
        await Assert.ThrowsAsync<TicketImprovementConflictException>(() => tickets.SplitTicketAsync(project.Slug, original, drafts));
        Assert.Equal(2, (await tickets.GetTicketAsync(project.Slug, original.Id))!.SubTickets.Count);
    }

    [Fact]
    public async Task Split_InvalidDraftDoesNotCreateAnyTickets()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("invalid-split");
        var tickets = new TicketService(projects, new MemberService(projects));
        var original = await tickets.CreateTicketAsync(project.Slug, "Broad scope", "Original");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tickets.SplitTicketAsync(project.Slug, original,
            [new("First", "Scope", ["Result"]), new("Invalid", "Scope", [" "])]));
        var after = (await tickets.GetTicketAsync(project.Slug, original.Id))!;
        Assert.Empty(after.SubTickets);
        Assert.Equal(original.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Split_DatabaseFailureRollsBackEntireBatchAndDoesNotNotifyCreation()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("rollback-split");
        var tickets = new TicketService(projects, new MemberService(projects));
        var original = await tickets.CreateTicketAsync(project.Slug, "Broad scope", "Original");
        await using (var db = projects.GetProjectDb(project.Slug))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_split_test BEFORE INSERT ON Tickets
                WHEN NEW.Title = 'Rejected' BEGIN SELECT RAISE(ABORT, 'rejected'); END;
                """);
        var created = new List<int>();
        tickets.TicketCreated += (_, id) => created.Add(id);
        await Assert.ThrowsAsync<DbUpdateException>(() => tickets.SplitTicketAsync(project.Slug, original,
            [new("First", "Scope", ["Result"]), new("Rejected", "Scope", ["Result"])]));
        var after = (await tickets.GetTicketAsync(project.Slug, original.Id))!;
        Assert.Empty(after.SubTickets);
        Assert.Empty(created);
        Assert.Equal(original.UpdatedAt, after.UpdatedAt);
    }

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
        Assert.Equal(AcceptanceStatus.Pending, after.AcceptanceCriteria[1].Status);
        Assert.Null(after.AcceptanceCriteria[1].Evidence);
        await Assert.ThrowsAsync<TicketImprovementConflictException>(() => tickets.ApplyImprovementAsync(project.Slug,
            ticket.Id, before.Description, before.AcceptanceCriteriaJson, "Stale", ["Wrong"]));
        Assert.Equal("Improved", (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.Description);
    }

    [Fact]
    public async Task Apply_DatabaseFailureRollsBackDescriptionCriteriaRevisionAndActivity()
    {
        using var tmp = new TempDir();
        var projects = new ProjectService(tmp.Path);
        var project = await projects.CreateProjectAsync("atomic-improvement");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Original", "Original scope");
        await tickets.SaveAcceptanceCriterionAsync(project.Slug, ticket.Id, null,
            new("Existing", AcceptanceStatus.PASS, new("Proof")));
        var before = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        await using (var db = projects.GetProjectDb(project.Slug))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_improvement_test BEFORE UPDATE ON Tickets
                WHEN NEW.Description = 'Rejected' BEGIN SELECT RAISE(ABORT, 'rejected'); END;
                """);

        await Assert.ThrowsAsync<DbUpdateException>(() => tickets.ApplyImprovementAsync(project.Slug,
            ticket.Id, before.Description, before.AcceptanceCriteriaJson, "Rejected", ["New result"], before.UpdatedAt));

        var after = (await tickets.GetTicketAsync(project.Slug, ticket.Id))!;
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.AcceptanceCriteriaJson, after.AcceptanceCriteriaJson);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Activities.Count, after.Activities.Count);
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
