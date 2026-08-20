using KittyClaw.Core.Automation;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace KittyClaw.Core.Tests.Automation;

public sealed class TicketCommentSteeringServiceTests
{
    [Fact]
    public async Task Owner_comment_is_forwarded_to_every_active_run_for_the_ticket()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("comment-steering");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Active work", "owner", "Todo");
        var runs = new AgentRunRegistry();
        var first = NewRun("first", project.Slug, ticket.Id);
        var second = NewRun("second", project.Slug, ticket.Id);
        runs.Register(first);
        runs.Register(second);
        await using var service = new StartedService(tickets, runs);

        var comment = await tickets.AddCommentAsync(project.Slug, ticket.Id, "Please also cover the restart case.");

        Assert.NotNull(comment);
        Assert.True(first.SteeringQueue.Reader.TryRead(out var firstMessage));
        Assert.True(second.SteeringQueue.Reader.TryRead(out var secondMessage));
        Assert.Equal(firstMessage, secondMessage);
        Assert.Contains($"comment #{comment!.Id}", firstMessage);
        Assert.Contains("Please also cover the restart case.", firstMessage);
    }

    [Fact]
    public async Task Non_owner_comment_is_not_forwarded()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("comment-loop-guard");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Active work", "owner", "Todo");
        var runs = new AgentRunRegistry();
        var run = NewRun("active", project.Slug, ticket.Id);
        runs.Register(run);
        await using var service = new StartedService(tickets, runs);

        await tickets.AddCommentAsync(project.Slug, ticket.Id, "Implementation evidence", "programmer");

        Assert.False(run.SteeringQueue.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Owner_comment_is_not_forwarded_to_other_or_completed_runs()
    {
        using var temp = new TempDir();
        var projects = new ProjectService(temp.Path);
        var project = await projects.CreateProjectAsync("comment-scope");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Active work", "owner", "Todo");
        var runs = new AgentRunRegistry();
        var otherTicket = NewRun("other-ticket", project.Slug, ticket.Id + 1);
        var completed = NewRun("completed", project.Slug, ticket.Id);
        runs.Register(otherTicket);
        runs.Register(completed);
        runs.Complete(completed.RunId, AgentRunStatus.Completed, 0);
        await using var service = new StartedService(tickets, runs);

        await tickets.AddCommentAsync(project.Slug, ticket.Id, "Only active work should receive this.");

        Assert.False(otherTicket.SteeringQueue.Reader.TryRead(out _));
        Assert.False(completed.SteeringQueue.Reader.TryRead(out _));
    }

    private static AgentRun NewRun(string runId, string projectSlug, int ticketId) => new()
    {
        RunId = runId,
        ProjectSlug = projectSlug,
        TicketId = ticketId,
        AgentName = "programmer",
        SkillFile = "programmer/SKILL.md",
        ConcurrencyGroup = "programmer",
        StartedAt = DateTime.UtcNow,
    };

    private sealed class StartedService : IAsyncDisposable
    {
        private readonly TicketCommentSteeringService _service;

        public StartedService(TicketService tickets, AgentRunRegistry runs)
        {
            _service = new TicketCommentSteeringService(
                tickets,
                runs,
                NullLogger<TicketCommentSteeringService>.Instance);
            _service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync() =>
            await _service.StopAsync(CancellationToken.None);
    }
}
