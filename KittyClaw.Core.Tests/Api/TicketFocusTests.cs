using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KittyClaw.Core.Tests.Api;

public sealed class TicketFocusTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "kittyclaw-focus-" + Guid.NewGuid());

    [Theory]
    [InlineData("/board/source", true)]
    [InlineData("/board/source/settings", true)]
    [InlineData("/board/source/sub/42", true)]
    [InlineData("/board/source-other", false)]
    [InlineData("/board/destination", false)]
    [InlineData("/", false)]
    public void FocusOnlyMatchesViewsOfTheRequestedProject(string path, bool matches) =>
        Assert.Equal(matches, BoardFocusService.MatchesProjectView(path, "source"));

    [Fact]
    public async Task FocusPublishesNavigationWithoutChangingTicketAndCanRepeat()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Focus");
        var tickets = new TicketService(projects, new MemberService(projects));
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent", "", "owner");
        var child = await tickets.CreateTicketAsync(project.Slug, "Child", "", "owner", parentId: parent.Id);
        var focus = new BoardFocusService();
        var requests = new List<(string Slug, string Url)>();
        focus.Requested += (slug, url) => requests.Add((slug, url));
        var before = await tickets.GetTicketAsync(project.Slug, child.Id);
        var first = Assert.IsType<Ok<TicketFocusResponse>>(await Endpoints.HandleFocusTicket(project.Slug, child.Id, tickets, focus, projects));
        var second = Assert.IsType<Ok<TicketFocusResponse>>(await Endpoints.HandleFocusTicket(project.Slug, child.Id, tickets, focus, projects));
        Assert.Equal(child.Id, first.Value!.TicketId);
        Assert.Contains($"/sub/{parent.Id}?pipeline={child.PipelineId}&focus={child.Id}&", first.Value.Url);
        Assert.DoesNotContain("/ticket/", first.Value.Url);
        Assert.NotEqual(first.Value.Url, second.Value!.Url);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(project.Slug, request.Slug));
        var after = await tickets.GetTicketAsync(project.Slug, child.Id);
        Assert.Equal(before!.UpdatedAt, after!.UpdatedAt);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.AssignedTo, after.AssignedTo);
    }

    [Fact]
    public async Task MissingTicketOrProjectDoesNotPublishFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Missing");
        var tickets = new TicketService(projects, new MemberService(projects));
        var focus = new BoardFocusService();
        var requests = 0;
        focus.Requested += (_, _) => requests++;
        Assert.IsType<NotFound>(await Endpoints.HandleFocusTicket(project.Slug, int.MaxValue, tickets, focus, projects));
        Assert.IsType<NotFound>(await Endpoints.HandleFocusTicket("absent-project", 1, tickets, focus, projects));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task McpFocusUsesSameNavigationAndReturnsUsableUrl()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("MCP focus");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Focus through MCP");
        var focus = new BoardFocusService();
        string? publishedUrl = null;
        focus.Requested += (_, url) => publishedUrl = url;
        using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(focus).BuildServiceProvider();
        var result = await new McpManagementTools(services, projects).FocusTicket(project.Slug, ticket.Id);
        Assert.Equal(ticket.Id, result.GetProperty("ticketId").GetInt32());
        Assert.Equal(publishedUrl, result.GetProperty("url").GetString());
        Assert.Contains($"focus={ticket.Id}&", publishedUrl);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            new McpManagementTools(services, projects).FocusTicket(project.Slug, int.MaxValue));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreationFocusIsOptInAndTargetsTheCreatedChild(bool mcp, bool enableFocus)
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Create with focus");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<(string Slug, string Url)>();
        focus.Requested += (slug, url) => requests.Add((slug, url));
        Ticket created;
        if (mcp)
        {
            var tools = new McpTools(projects, tickets, new ColumnService(projects), members, notifier, focus);
            created = enableFocus
                ? await tools.CreateTicket(project.Slug, "New child", parentId: parent.Id, focus: true)
                : await tools.CreateTicket(project.Slug, "New child", parentId: parent.Id);
        }
        else
        {
            var request = enableFocus
                ? new CreateTicketRequest("New child", "owner", "Backlog", ParentId: parent.Id, Focus: true)
                : new CreateTicketRequest("New child", "owner", "Backlog", ParentId: parent.Id);
            var result = Assert.IsType<Created<Ticket>>(await Endpoints.HandleCreateTicket(project.Slug, request, tickets, notifier, focus));
            created = result.Value!;
            Assert.Equal($"/api/projects/{project.Slug}/tickets/{created.Id}", result.Location);
        }
        Assert.NotNull(await tickets.GetTicketAsync(project.Slug, created.Id));
        if (enableFocus)
        {
            var request = Assert.Single(requests);
            Assert.Equal(project.Slug, request.Slug);
            Assert.Contains($"/sub/{parent.Id}?pipeline={created.PipelineId}&focus={created.Id}&", request.Url);
            Assert.DoesNotContain("/ticket/", request.Url);
        }
        else Assert.Empty(requests);
    }

    [Fact]
    public async Task FailedCreationDoesNotRequestFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Invalid creation");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var focus = new BoardFocusService();
        var requests = 0;
        focus.Requested += (_, _) => requests++;
        var notifier = new BoardUpdateNotifier();
        var result = await Endpoints.HandleCreateTicket(project.Slug,
            new CreateTicketRequest("Invalid", "owner", "Missing column", Focus: true), tickets, notifier, focus);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        var tools = new McpTools(projects, tickets, new ColumnService(projects), members, notifier, focus);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.CreateTicket(
            project.Slug, "Invalid", status: "Missing column", focus: true));
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("reorder", false)]
    [InlineData("reorder", true)]
    [InlineData("mcp-move", false)]
    [InlineData("mcp-move", true)]
    [InlineData("mcp-update", false)]
    [InlineData("mcp-update", true)]
    [InlineData("mcp-reorder", false)]
    [InlineData("mcp-reorder", true)]
    public async Task MoveFocusUsesDestinationAndRemainsOptIn(string operation, bool enableFocus)
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Move focus");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Move me");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<string>();
        focus.Requested += (_, url) => requests.Add(url);
        using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
        var management = new McpManagementTools(services, projects);
        var update = new UpdateTicketRequest("owner", Status: "Todo", Focus: enableFocus);
        var reorder = new ReorderTicketRequest("Todo", 0, enableFocus);
        switch (operation)
        {
            case "status":
                Assert.IsType<Ok<Ticket>>(await Endpoints.HandleMoveTicket(project.Slug, ticket.Id,
                    new MoveTicketRequest("Todo", "owner", enableFocus), tickets, notifier, focus));
                break;
            case "update":
                Assert.IsType<Ok<Ticket>>(await Endpoints.HandleUpdateTicket(project.Slug, ticket.Id, update, tickets, notifier, focus));
                break;
            case "reorder":
                Assert.IsType<NoContent>(await Endpoints.HandleReorderTicket(project.Slug, ticket.Id, reorder, tickets, notifier, focus));
                break;
            case "mcp-move":
                await new McpTools(projects, tickets, new ColumnService(projects), members, notifier, focus)
                    .MoveTicket(project.Slug, ticket.Id, "Todo", focus: enableFocus);
                break;
            case "mcp-update": await management.UpdateTicket(project.Slug, ticket.Id, update); break;
            case "mcp-reorder": await management.ReorderTicket(project.Slug, ticket.Id, reorder); break;
        }
        var moved = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        Assert.Equal("Todo", moved!.Status);
        Assert.NotEqual(ticket.ColumnId, moved.ColumnId);
        if (enableFocus)
            Assert.Contains($"pipeline={moved.PipelineId}&focus={moved.Id}&", Assert.Single(requests));
        else Assert.Empty(requests);
    }

    [Fact]
    public async Task CrossPipelineMoveFocusUsesNewPipelineAndFailuresDoNotFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Cross-pipeline focus");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Move me");
        var pipeline = await new PipelineService(projects).CreateAsync(project.Slug, "Other");
        var target = await new ColumnService(projects).CreateColumnAsync(project.Slug, "Analyze", pipelineId: pipeline.Id);
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<string>();
        focus.Requested += (_, url) => requests.Add(url);
        var conflict = await Endpoints.HandleUpdateTicket(project.Slug, ticket.Id,
            new UpdateTicketRequest("owner", Status: "Todo", ExpectedStatus: "Done", Focus: true), tickets, notifier, focus);
        Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)conflict).StatusCode);
        var invalid = await Endpoints.HandleMoveTicket(project.Slug, ticket.Id,
            new MoveTicketRequest("Missing column", "owner", true), tickets, notifier, focus);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)invalid).StatusCode);
        Assert.IsType<NotFound>(await Endpoints.HandleMoveTicket(project.Slug, int.MaxValue,
            new MoveTicketRequest("Todo", "owner", true), tickets, notifier, focus));
        var tools = new McpTools(projects, tickets, new ColumnService(projects), members, notifier, focus);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.MoveTicket(project.Slug, ticket.Id, "Missing column", focus: true));
        Assert.Empty(requests);
        var result = Assert.IsType<Ok<Ticket>>(await Endpoints.HandleUpdateTicket(project.Slug, ticket.Id,
            new UpdateTicketRequest("owner", PipelineId: pipeline.Id, ColumnId: target.Id, Focus: true), tickets, notifier, focus));
        Assert.Equal(target.Id, result.Value!.ColumnId);
        Assert.Contains($"pipeline={pipeline.Id}&focus={ticket.Id}&", Assert.Single(requests));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TransferFocusTargetsDestinationProjectAndItsPipeline(bool mcp, bool enableFocus)
    {
        var projects = new ProjectService(directory);
        var source = await projects.CreateProjectAsync("Source");
        var target = await projects.CreateProjectAsync("Destination");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var columns = new ColumnService(projects);
        var transfers = new TicketTransferService(projects, tickets, columns, members, new LabelService(projects));
        var pipeline = await new PipelineService(projects).CreateAsync(source.Slug, "Source workflow");
        var sourceColumn = await columns.CreateColumnAsync(source.Slug, "Todo", pipelineId: pipeline.Id);
        var root = await tickets.CreateTicketAsync(source.Slug, "Transfer me", status: "Todo", pipelineId: pipeline.Id, columnId: sourceColumn.Id);
        var child = await tickets.CreateTicketAsync(source.Slug, "Child", status: "Todo", parentId: root.Id, pipelineId: pipeline.Id, columnId: sourceColumn.Id);
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<(string Slug, string Url)>();
        focus.Requested += (slug, url) => requests.Add((slug, url));
        var request = enableFocus
            ? new TransferTicketRequest(target.Slug, "owner", Focus: true)
            : new TransferTicketRequest(target.Slug, "owner");
        if (mcp)
        {
            using var services = new ServiceCollection().AddSingleton(transfers).AddSingleton(tickets)
                .AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
            var response = await new McpManagementTools(services, projects).TransferTicket(source.Slug, root.Id, request);
            Assert.Equal(root.Id, response.GetProperty("ticketId").GetInt32());
        }
        else
        {
            var response = Assert.IsType<Ok<TicketTransferResult>>(await Endpoints.HandleTransferTicket(
                source.Slug, root.Id, request, transfers, notifier, tickets, focus));
            Assert.Equal(root.Id, response.Value!.TicketId);
            Assert.Equal(2, response.Value.TicketCount);
        }
        Assert.Null(await tickets.GetTicketAsync(source.Slug, root.Id));
        var transferred = await tickets.GetTicketAsync(target.Slug, root.Id);
        Assert.NotNull(transferred);
        Assert.NotEqual(pipeline.Id, transferred.PipelineId);
        Assert.Equal(root.Id, (await tickets.GetTicketAsync(target.Slug, child.Id))!.ParentId);
        if (enableFocus)
        {
            Assert.Equal(2, requests.Count);
            Assert.Contains(requests, r => r.Slug == target.Slug);
            Assert.Contains(requests, r => r.Slug == source.Slug);
            Assert.All(requests, r =>
            {
                Assert.StartsWith($"/board/{target.Slug}?pipeline={transferred.PipelineId}&focus={root.Id}&", r.Url);
                Assert.DoesNotContain("/ticket/", r.Url);
            });
        }
        else Assert.Empty(requests);
    }

    [Fact]
    public async Task FailedTransferDoesNotRequestFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Source");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var transfers = new TicketTransferService(projects, tickets, new ColumnService(projects), members, new LabelService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Keep me");
        var notifier = new BoardUpdateNotifier();
        var focus = new BoardFocusService();
        var requests = 0;
        focus.Requested += (_, _) => requests++;
        var request = new TransferTicketRequest(project.Slug, "owner", Focus: true);
        var result = await Endpoints.HandleTransferTicket(project.Slug, ticket.Id, request, transfers, notifier, tickets, focus);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        using var services = new ServiceCollection().AddSingleton(transfers).AddSingleton(tickets)
            .AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            new McpManagementTools(services, projects).TransferTicket(project.Slug, ticket.Id, request));
        Assert.Equal(0, requests);
        Assert.NotNull(await tickets.GetTicketAsync(project.Slug, ticket.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScheduleFocusHighlightsScheduledAndRemainsOptIn(bool mcp, bool enableFocus)
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Schedule focus");
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent");
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Schedule me", parentId: parent.Id);
        var fireAt = DateTime.UtcNow.AddDays(2);
        var request = enableFocus
            ? new ScheduleTicketRequest(fireAt, "owner", "Todo", Focus: true)
            : new ScheduleTicketRequest(fireAt, "owner", "Todo");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<string>();
        focus.Requested += (_, url) => requests.Add(url);
        if (mcp)
        {
            using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
            var result = await new McpManagementTools(services, projects).ScheduleTicket(project.Slug, ticket.Id, request);
            Assert.Equal("Scheduled", result.GetProperty("status").GetString());
        }
        else
        {
            var result = Assert.IsType<Ok<Ticket>>(await Endpoints.HandleScheduleTicket(project.Slug, ticket.Id, request, tickets, notifier, focus));
            Assert.Equal("Scheduled", result.Value!.Status);
        }
        var scheduled = await tickets.GetTicketAsync(project.Slug, ticket.Id);
        var column = (await new ColumnService(projects).ListColumnsAsync(project.Slug)).Single(c => c.Name == "Scheduled");
        Assert.Equal(column.Id, scheduled!.ColumnId);
        Assert.Equal(fireAt, scheduled.FireAt);
        Assert.Equal("Todo", scheduled.ScheduleTarget);
        if (enableFocus)
            Assert.StartsWith($"/board/{project.Slug}/sub/{parent.Id}?pipeline={scheduled.PipelineId}&focus={ticket.Id}&", Assert.Single(requests));
        else Assert.Empty(requests);
    }

    [Fact]
    public async Task InvalidSchedulingAndMissingTicketsDoNotRequestFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Invalid schedule");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Keep me");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = 0;
        focus.Requested += (_, _) => requests++;
        var invalid = new ScheduleTicketRequest(DateTime.UtcNow.AddDays(2), "owner", "Missing column", Focus: true);
        var result = await Endpoints.HandleScheduleTicket(project.Slug, ticket.Id, invalid, tickets, notifier, focus);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        var valid = new ScheduleTicketRequest(DateTime.UtcNow.AddDays(2), "owner", Focus: true);
        Assert.IsType<NotFound>(await Endpoints.HandleScheduleTicket(project.Slug, int.MaxValue, valid, tickets, notifier, focus));
        using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
        var tools = new McpManagementTools(services, projects);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.ScheduleTicket(project.Slug, ticket.Id, invalid));
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.ScheduleTicket(project.Slug, int.MaxValue, valid));
        Assert.Equal(0, requests);
        Assert.Equal("Backlog", (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParentFocusFollowsAssociationAndDetachment(bool mcp, bool enableFocus)
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Parent focus");
        var tickets = new TicketService(projects, new MemberService(projects));
        var grandparent = await tickets.CreateTicketAsync(project.Slug, "Grandparent");
        var parent = await tickets.CreateTicketAsync(project.Slug, "Parent", parentId: grandparent.Id);
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Child");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = new List<string>();
        focus.Requested += (_, url) => requests.Add(url);
        using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
        var tools = new McpManagementTools(services, projects);
        var request = enableFocus ? new SetParentRequest(parent.Id, Focus: true) : new SetParentRequest(parent.Id);
        if (mcp) await tools.SetTicketParent(project.Slug, ticket.Id, request);
        else Assert.IsType<NoContent>(await Endpoints.HandleSetTicketParent(project.Slug, ticket.Id, request, tickets, notifier, focus));
        Assert.Equal(parent.Id, (await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ParentId);
        if (enableFocus)
            Assert.StartsWith($"/board/{project.Slug}/sub/{parent.Id}?pipeline={ticket.PipelineId}&focus={ticket.Id}&", Assert.Single(requests));
        else Assert.Empty(requests);
        requests.Clear();
        if (mcp)
        {
            if (enableFocus) await tools.RemoveTicketParent(project.Slug, ticket.Id, focus: true);
            else await tools.RemoveTicketParent(project.Slug, ticket.Id);
        }
        else Assert.IsType<NoContent>(await Endpoints.HandleRemoveTicketParent(project.Slug, ticket.Id, tickets, notifier, focus, enableFocus));
        Assert.Null((await tickets.GetTicketAsync(project.Slug, ticket.Id))!.ParentId);
        if (enableFocus)
            Assert.StartsWith($"/board/{project.Slug}?pipeline={ticket.PipelineId}&focus={ticket.Id}&", Assert.Single(requests));
        else Assert.Empty(requests);
    }

    [Fact]
    public async Task RejectedParentOperationsDoNotFocus()
    {
        var projects = new ProjectService(directory);
        var project = await projects.CreateProjectAsync("Rejected parent focus");
        var tickets = new TicketService(projects, new MemberService(projects));
        var ticket = await tickets.CreateTicketAsync(project.Slug, "Root");
        var focus = new BoardFocusService();
        var notifier = new BoardUpdateNotifier();
        var requests = 0;
        focus.Requested += (_, _) => requests++;
        var request = new SetParentRequest(ticket.Id, Focus: true);
        var result = await Endpoints.HandleSetTicketParent(project.Slug, ticket.Id, request, tickets, notifier, focus);
        Assert.Equal(400, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode);
        Assert.IsType<NotFound>(await Endpoints.HandleRemoveTicketParent(project.Slug, ticket.Id, tickets, notifier, focus, true));
        using var services = new ServiceCollection().AddSingleton(tickets).AddSingleton(notifier).AddSingleton(focus).BuildServiceProvider();
        var tools = new McpManagementTools(services, projects);
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.SetTicketParent(project.Slug, ticket.Id, request));
        await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.RemoveTicketParent(project.Slug, int.MaxValue, focus: true));
        Assert.Equal(0, requests);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
