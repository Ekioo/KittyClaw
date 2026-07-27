using KittyClaw.Core.Services;
using KittyClaw.Core.Tests.Helpers;

namespace KittyClaw.Core.Tests.Services;

public sealed class CommentValidationTests
{
    private static (TicketService tickets, string slug, int ticketId) BuildTicket(TempDir tmp)
    {
        var projects = new ProjectService(tmp.Path);
        var project = projects.CreateProjectAsync("comment-val").GetAwaiter().GetResult();
        var members = new MemberService(projects);
        var tickets = new TicketService(projects, members);
        var ticket = tickets.CreateTicketAsync(project.Slug, "T1").GetAwaiter().GetResult();
        return (tickets, project.Slug, ticket.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddComment_NullOrWhitespaceContent_Throws(string? content)
    {
        using var tmp = new TempDir();
        var (tickets, slug, id) = BuildTicket(tmp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tickets.AddCommentAsync(slug, id, content, "owner"));
        Assert.Contains("content", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddComment_ValidContent_Persists()
    {
        using var tmp = new TempDir();
        var (tickets, slug, id) = BuildTicket(tmp);

        var c = await tickets.AddCommentAsync(slug, id, "  hello  ", "owner");
        Assert.NotNull(c);
        Assert.Equal("hello", c!.Content);
    }
}
