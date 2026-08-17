using KittyClaw.Core.Models;
using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;
using KittyClaw.Web.Api;
using KittyClaw.Web.Services;
using ModelContextProtocol;

namespace KittyClaw.Core.Tests.Api;

/// <summary>
/// The 7 MCP tools (ticket #215) are thin proxies over the same services the REST API uses.
/// These tests exercise the handlers directly against real services on a temp data dir:
/// happy paths, the project-existence guard (the REST layer silently creates an empty db
/// for unknown slugs — MCP agents get a real error instead), and the McpException wrapping
/// that surfaces service validation messages to the calling agent verbatim.
/// </summary>
public sealed class McpToolsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProjectService _projects;
    private readonly MemberService _members;
    private readonly ColumnService _columns;
    private readonly TicketService _tickets;
    private readonly BoardUpdateNotifier _notifier = new();
    private readonly McpTools _tools;

    public McpToolsTests()
    {
        _projects = new ProjectService(_dir.Path);
        _members = new MemberService(_projects);
        _columns = new ColumnService(_projects);
        _tickets = new TicketService(_projects, _members);
        _tools = new McpTools(_projects, _tickets, _columns, _members, _notifier);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<string> CreateProjectAsync() =>
        (await _projects.CreateProjectAsync("mcp tools " + Guid.NewGuid().ToString("N")[..8])).Slug;

    // ── Read tools ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListProjects_ReturnsCreatedProjects()
    {
        var slug = await CreateProjectAsync();

        var projects = await _tools.ListProjects();

        Assert.Contains(projects, p => p.Slug == slug);
    }

    [Fact]
    public async Task ListTickets_FiltersByStatusAndAssignee()
    {
        var slug = await CreateProjectAsync();
        var member = await _members.CreateMemberAsync(slug, "Programmer");
        await _tickets.CreateTicketAsync(slug, "In todo, assigned", status: "Todo", assignedTo: member.Slug);
        await _tickets.CreateTicketAsync(slug, "In todo, unassigned", status: "Todo");
        await _tickets.CreateTicketAsync(slug, "In backlog", status: "Backlog");

        Assert.Equal(3, (await _tools.ListTickets(slug)).Count);
        Assert.Equal(2, (await _tools.ListTickets(slug, status: "Todo")).Count);
        var assigned = await _tools.ListTickets(slug, status: "Todo", assignee: member.Slug);
        var single = Assert.Single(assigned);
        Assert.Equal("In todo, assigned", single.Title);
    }

    [Fact]
    public async Task GetTicket_ReturnsFullTicketWithComments()
    {
        var slug = await CreateProjectAsync();
        var created = await _tickets.CreateTicketAsync(slug, "Detailed", "Body text", status: "Todo");
        await _tickets.AddCommentAsync(slug, created.Id, "First comment", "lain");

        var ticket = await _tools.GetTicket(slug, created.Id);

        Assert.Equal("Detailed", ticket.Title);
        Assert.Equal("Body text", ticket.Description);
        var comment = Assert.Single(ticket.Comments);
        Assert.Equal("First comment", comment.Content);
        Assert.Equal("lain", comment.Author);
    }

    [Fact]
    public async Task GetTicket_UnknownId_ThrowsMcpException()
    {
        var slug = await CreateProjectAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.GetTicket(slug, 999));

        Assert.Contains("#999", ex.Message);
        Assert.Contains(slug, ex.Message);
    }

    [Fact]
    public async Task BoardOverview_ReturnsColumnsAndSeededOwnerMember()
    {
        var slug = await CreateProjectAsync();

        var overview = await _tools.GetBoardOverview(slug);

        Assert.Equal(slug, overview.Slug);
        Assert.Contains(overview.Columns, c => c.Name == "Todo");
        Assert.Contains(overview.Members, m => m.Slug == "owner");
    }

    // ── Unknown-project guard ────────────────────────────────────────────────

    [Fact]
    public async Task Tools_UnknownProject_ThrowActionableMcpException()
    {
        foreach (var call in new Func<Task>[]
        {
            () => _tools.ListTickets("no-such-project"),
            () => _tools.GetTicket("no-such-project", 1),
            () => _tools.CreateTicket("no-such-project", "t"),
            () => _tools.CommentTicket("no-such-project", 1, "c"),
            () => _tools.MoveTicket("no-such-project", 1, "Todo"),
            () => _tools.GetBoardOverview("no-such-project"),
        })
        {
            var ex = await Assert.ThrowsAsync<McpException>(call);
            Assert.Contains("list_projects", ex.Message);
        }
        // The guard must reject BEFORE GetProjectDb conjures an empty db file for the slug.
        Assert.False(File.Exists(Path.Combine(_dir.Path, "projects", "no-such-project.db")));
    }

    // ── Write tools ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTicket_AppliesRestDefaults_AndNotifiesBoard()
    {
        var slug = await CreateProjectAsync();
        var notified = new List<string>();
        _notifier.OnProjectUpdated += notified.Add;

        var ticket = await _tools.CreateTicket(slug, "From MCP");

        Assert.Equal("Backlog", ticket.Status);
        Assert.Equal(TicketPriority.NiceToHave, ticket.Priority);
        Assert.Equal("owner", ticket.CreatedBy);
        Assert.Contains(slug, notified);
    }

    [Fact]
    public async Task CreateTicket_HonorsExplicitFields()
    {
        var slug = await CreateProjectAsync();
        var member = await _members.CreateMemberAsync(slug, "Programmer");

        var ticket = await _tools.CreateTicket(
            slug, "Groomed", "Details", status: "Todo",
            priority: TicketPriority.Required, assignee: member.Slug, author: "lain");

        Assert.Equal("Todo", ticket.Status);
        Assert.Equal(TicketPriority.Required, ticket.Priority);
        Assert.Equal(member.Slug, ticket.AssignedTo);
        Assert.Equal("lain", ticket.CreatedBy);
    }

    [Fact]
    public async Task CreateTicket_UnknownStatus_SurfacesServiceErrorAsMcpException()
    {
        var slug = await CreateProjectAsync();

        await Assert.ThrowsAsync<McpException>(() =>
            _tools.CreateTicket(slug, "t", status: "NoSuchColumn"));
    }

    [Fact]
    public async Task CommentTicket_PersistsComment_AndNotifiesBoard()
    {
        var slug = await CreateProjectAsync();
        var created = await _tickets.CreateTicketAsync(slug, "Commented", status: "Todo");
        var notified = new List<string>();
        _notifier.OnProjectUpdated += notified.Add;

        var comment = await _tools.CommentTicket(slug, created.Id, "Done, see files.", "programmer");

        Assert.Equal("programmer", comment.Author);
        var ticket = await _tickets.GetTicketAsync(slug, created.Id);
        Assert.Contains(ticket!.Comments, c => c.Content == "Done, see files.");
        Assert.Contains(slug, notified);
    }

    [Fact]
    public async Task CommentTicket_EmptyContent_ThrowsMcpException()
    {
        var slug = await CreateProjectAsync();
        var created = await _tickets.CreateTicketAsync(slug, "t", status: "Todo");

        await Assert.ThrowsAsync<McpException>(() => _tools.CommentTicket(slug, created.Id, "  "));
    }

    [Fact]
    public async Task CommentTicket_UnknownTicket_ThrowsMcpException()
    {
        var slug = await CreateProjectAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.CommentTicket(slug, 999, "c"));

        Assert.Contains("#999", ex.Message);
    }

    [Fact]
    public async Task MoveTicket_MovesAndLogsActivity_AndNotifiesBoard()
    {
        var slug = await CreateProjectAsync();
        var created = await _tickets.CreateTicketAsync(slug, "Movable", status: "Todo");
        var notified = new List<string>();
        _notifier.OnProjectUpdated += notified.Add;

        var moved = await _tools.MoveTicket(slug, created.Id, "InProgress", "automation");

        Assert.Equal("InProgress", moved.Status);
        Assert.Contains(slug, notified);
        var ticket = await _tickets.GetTicketAsync(slug, created.Id);
        Assert.Contains(ticket!.Activities, a => a.Text.Contains("Todo → InProgress"));
    }

    [Fact]
    public async Task MoveTicket_UnknownStatus_SurfacesServiceErrorAsMcpException()
    {
        var slug = await CreateProjectAsync();
        var created = await _tickets.CreateTicketAsync(slug, "t", status: "Todo");

        await Assert.ThrowsAsync<McpException>(() => _tools.MoveTicket(slug, created.Id, "NoSuchColumn"));
    }

    [Fact]
    public async Task MoveTicket_UnknownTicket_ThrowsMcpException()
    {
        var slug = await CreateProjectAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.MoveTicket(slug, 999, "Todo"));

        Assert.Contains("#999", ex.Message);
    }
}
