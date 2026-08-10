using System.Text.Json;
using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class MinimalWorkflowServiceTests
{
    [Fact]
    public async Task EnsureAsync_CreatesMinimalJourneyAndIsIdempotent()
    {
        using var tmp = new TempDir();
        var (service, projects, tickets, columns, processors) = CreateServices(tmp.Path);
        var project = await projects.CreateProjectAsync("minimal-flow");
        var ticket = await tickets.CreateTicketAsync(project.Slug, "First result", "Do the work", "owner");

        var first = await service.EnsureAsync(project.Slug, ticket.Id, "journey-1");
        var second = await service.EnsureAsync(project.Slug, ticket.Id, "journey-1");

        Assert.Equal(first, second);
        var workflowColumns = await columns.ListColumnsAsync(project.Slug, first.PipelineId);
        Assert.Equal(4, workflowColumns.Count);
        Assert.Equal(ColumnRole.OwnerAction, workflowColumns.Single(c => c.Name == "Human decision").Role);
        var configured = await processors.ListAsync(project.Slug);
        Assert.Equal(3, configured.Count(p => workflowColumns.Any(c => c.Id == p.ColumnId)));
        Assert.Equal(first.ImplementColumnId, configured.Single(p => p.ColumnId == first.QualifyColumnId).DefaultTargetColumnId);
        Assert.Equal(first.VerifyColumnId, configured.Single(p => p.ColumnId == first.ImplementColumnId).DefaultTargetColumnId);
        Assert.Equal(first.HumanDecisionColumnId, configured.Single(p => p.ColumnId == first.VerifyColumnId).DefaultTargetColumnId);
        Assert.All(configured.Where(p => workflowColumns.Any(c => c.Id == p.ColumnId)), p => Assert.Null(p.Model));
        var persisted = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.Equal("Qualify", persisted!.Status);
        Assert.Equal(first.QualifyColumnId, persisted.ColumnId);
        Assert.Equal(2, ReadEvents(tmp.Path).Count(e => e.Name == "minimal_workflow_ready"));
    }

    [Fact]
    public async Task EnsureAsync_WhenTicketIsMissing_RecordsRecoverableFailureStep()
    {
        using var tmp = new TempDir();
        var (service, projects, tickets, columns, processors) = CreateServices(tmp.Path);
        var project = await projects.CreateProjectAsync("minimal-flow-failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(project.Slug, 404, "journey-failed"));

        var failed = Assert.Single(ReadEvents(tmp.Path), e => e.Name == "minimal_workflow_failed");
        Assert.Equal("journey-failed", failed.JourneyId);
        Assert.Equal("first-ticket", failed.FailedStep);
        Assert.NotNull(failed.DurationMilliseconds);

        var replacement = await tickets.CreateTicketAsync(project.Slug, "Replacement result", "Recover the flow", "owner");
        var recovered = await service.EnsureAsync(project.Slug, replacement.Id, "journey-recovered");

        Assert.Equal(4, (await columns.ListColumnsAsync(project.Slug, recovered.PipelineId)).Count);
        Assert.Equal(3, (await processors.ListAsync(project.Slug)).Count(p =>
            p.ColumnId == recovered.QualifyColumnId ||
            p.ColumnId == recovered.ImplementColumnId ||
            p.ColumnId == recovered.VerifyColumnId));
        Assert.Equal("Qualify", (await tickets.GetTicketAsync(project.Slug, replacement.Id))!.Status);
        Assert.Contains(ReadEvents(tmp.Path), e =>
            e.Name == "minimal_workflow_ready" && e.JourneyId == "journey-recovered");
    }

    private static (MinimalWorkflowService Service, ProjectService Projects, TicketService Tickets,
        ColumnService Columns, ColumnProcessorService Processors) CreateServices(string root)
    {
        var projects = new ProjectService(root);
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var columns = new ColumnService(projects);
        var processors = new ColumnProcessorService(projects, new ProjectSkillService(projects));
        return (new MinimalWorkflowService(root, new PipelineService(projects), columns, processors, tickets),
            projects, tickets, columns, processors);
    }

    private static List<MinimalWorkflowEvent> ReadEvents(string root) =>
        File.ReadAllLines(Path.Combine(root, "activation", "minimal-workflow-events.jsonl"))
            .Select(line => JsonSerializer.Deserialize<MinimalWorkflowEvent>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToList();
}
