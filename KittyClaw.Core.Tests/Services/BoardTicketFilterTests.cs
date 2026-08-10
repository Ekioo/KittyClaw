using KittyClaw.Core.Models;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Services;

public class BoardTicketFilterTests
{
    private static TicketSummary Make(int id, string title, string description = "",
        string? assignedTo = null, string createdBy = "owner",
        TicketPriority priority = TicketPriority.NiceToHave,
        DateTime? updatedAt = null,
        List<Label>? labels = null) =>
        new(id, title, description, "Todo", priority, 0, assignedTo, createdBy,
            DateTime.UtcNow, updatedAt ?? DateTime.UtcNow, labels ?? [], 0, null, null, []);

    [Fact]
    public void EmptyFilter_ReturnsAll()
    {
        var tickets = new[] { Make(1, "alpha"), Make(2, "beta") };
        var result = BoardTicketFilter.Apply(tickets, "").ToList();
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void HashId_MatchesExactId()
    {
        var tickets = new[] { Make(1, "alpha"), Make(12, "twelve"), Make(2, "beta") };
        var result = BoardTicketFilter.Apply(tickets, "#1").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void HashId_NoMatch_ReturnsEmpty()
    {
        var tickets = new[] { Make(1, "alpha"), Make(2, "beta") };
        var result = BoardTicketFilter.Apply(tickets, "#99").ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void AtToken_FiltersByAssignee()
    {
        var tickets = new[] { Make(1, "a", assignedTo: "alice"), Make(2, "b", assignedTo: "bob") };
        var result = BoardTicketFilter.Apply(tickets, "@ali").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void AtToken_UnassignedTickets_Excluded()
    {
        var tickets = new[] { Make(1, "a", assignedTo: null), Make(2, "b", assignedTo: "bob") };
        var result = BoardTicketFilter.Apply(tickets, "@bob").ToList();
        Assert.Single(result);
        Assert.Equal(2, result[0].Id);
    }

    [Fact]
    public void ByToken_FiltersByCreator()
    {
        var tickets = new[] { Make(1, "a", createdBy: "alice"), Make(2, "b", createdBy: "bob") };
        var result = BoardTicketFilter.Apply(tickets, "by:bob").ToList();
        Assert.Single(result);
        Assert.Equal(2, result[0].Id);
    }

    [Fact]
    public void FreeText_MatchesTitle()
    {
        var tickets = new[] { Make(1, "deploy hotfix"), Make(2, "refactor core") };
        var result = BoardTicketFilter.Apply(tickets, "hotfix").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void FreeText_MatchesDescription()
    {
        var tickets = new[] { Make(1, "ticket", "contains hotfix details"), Make(2, "other") };
        var result = BoardTicketFilter.Apply(tickets, "hotfix").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void FreeText_CaseInsensitive()
    {
        var tickets = new[] { Make(1, "Deploy Hotfix"), Make(2, "Other") };
        var result = BoardTicketFilter.Apply(tickets, "HOTFIX").ToList();
        Assert.Single(result);
    }

    [Fact]
    public void LabelFilter_MatchesLabelName()
    {
        var labels = new List<Label> { new() { Id = 1, Name = "bug", Color = "#f00" } };
        var tickets = new[] { Make(1, "a", labels: labels), Make(2, "b") };
        var result = BoardTicketFilter.Apply(tickets, "label:bug").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void PriorityFilter_MatchesPriorityClass()
    {
        var tickets = new[]
        {
            Make(1, "a", priority: TicketPriority.Critical),
            Make(2, "b", priority: TicketPriority.NiceToHave)
        };
        var result = BoardTicketFilter.Apply(tickets, "priority:critical").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void PriorityFilter_WithLabelResolver_MatchesLocalizedLabel()
    {
        string LocalizedLabel(TicketPriority p) => p == TicketPriority.Critical ? "Critique" : "Autre";
        var tickets = new[]
        {
            Make(1, "a", priority: TicketPriority.Critical),
            Make(2, "b", priority: TicketPriority.NiceToHave)
        };
        var result = BoardTicketFilter.Apply(tickets, "priority:Critique", LocalizedLabel).ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void DateAfter_ExcludesOlderTickets()
    {
        var old = Make(1, "old", updatedAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = Make(2, "fresh", updatedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = BoardTicketFilter.Apply([old, fresh], ">2026-01-01").ToList();
        Assert.Single(result);
        Assert.Equal(2, result[0].Id);
    }

    [Fact]
    public void DateBefore_ExcludesNewerTickets()
    {
        var old = Make(1, "old", updatedAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var fresh = Make(2, "fresh", updatedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = BoardTicketFilter.Apply([old, fresh], "<2026-01-01").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }

    [Fact]
    public void MultipleTokens_AreANDed()
    {
        var tickets = new[]
        {
            Make(1, "deploy alpha", assignedTo: "alice"),
            Make(2, "deploy beta", assignedTo: "bob"),
            Make(3, "refactor", assignedTo: "alice")
        };
        var result = BoardTicketFilter.Apply(tickets, "deploy @alice").ToList();
        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
    }
}
