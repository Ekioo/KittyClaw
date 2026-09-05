using KittyClaw.Core.Models;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class TicketNumberSearchTests
{
    [Theory]
    [InlineData("#42", 42)]
    [InlineData("42", 42)]
    [InlineData("project:kittyclaw #312", 312)]
    public void TryParse_FindsTicketToken(string query, int expected)
    {
        Assert.True(TicketNumberSearch.TryParse(query, out var ticketId));
        Assert.Equal(expected, ticketId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#missing")]
    public void TryParse_RejectsNonTicketQueries(string query) =>
        Assert.False(TicketNumberSearch.TryParse(query, out _));

    [Fact]
    public void Find_RootTicket_ReturnsOnlyTheExactTicket()
    {
        var result = TicketNumberSearch.Find([Ticket(42)], 42);

        var match = Assert.Single(result);
        Assert.Equal(42, match.Ticket.Id);
        Assert.False(match.IsParent);
    }

    [Fact]
    public void Find_SubTicket_ReturnsExactTicketThenItsParentWithoutDuplicates()
    {
        var result = TicketNumberSearch.Find([Ticket(10), Ticket(42, parentId: 10), Ticket(10)], 42);

        Assert.Collection(result,
            exact =>
            {
                Assert.Equal(42, exact.Ticket.Id);
                Assert.False(exact.IsParent);
            },
            parent =>
            {
                Assert.Equal(10, parent.Ticket.Id);
                Assert.True(parent.IsParent);
            });
    }

    [Fact]
    public void Find_UnknownTicket_ReturnsNoResult() =>
        Assert.Empty(TicketNumberSearch.Find([Ticket(10)], 999));

    private static TicketSummary Ticket(int id, int? parentId = null) => new(
        id, $"Ticket {id}", "", "Todo", TicketPriority.NiceToHave, 0, null, "owner",
        new DateTime(2026, 1, 1), new DateTime(2026, 1, 1), [], 0, null, parentId, []);
}
