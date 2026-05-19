using System;
using System.Collections.Generic;
using System.Linq;
using KittyClaw.Core.Models;
using KittyClaw.Web.Services;
using Xunit;

namespace KittyClaw.Core.Tests.Web;

public class BoardSortStateTests
{
    private static TicketSummary T(
        int id,
        string title = "t",
        TicketPriority priority = TicketPriority.NiceToHave,
        string? assignedTo = null,
        DateTime? createdAt = null,
        DateTime? dueDate = null)
        => new TicketSummary(
            Id: id,
            Title: title,
            Description: "",
            Status: "Todo",
            Priority: priority,
            SortOrder: 0,
            AssignedTo: assignedTo,
            CreatedBy: "owner",
            CreatedAt: createdAt ?? new DateTime(2026, 1, 1),
            UpdatedAt: new DateTime(2026, 1, 1),
            Labels: new List<Label>(),
            CommentCount: 0,
            LastActivityAt: null,
            ParentId: null,
            SubTickets: new List<SubTicketInfo>())
        {
            DueDate = dueDate
        };

    [Fact]
    public void Manual_returns_input_order_unchanged()
    {
        var t1 = T(1, "a");
        var t2 = T(2, "b");
        var t3 = T(3, "c");
        var input = new[] { t3, t1, t2 };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Manual, SortDirection.Ascending).ToList();

        Assert.Equal(new[] { t3, t1, t2 }, result);
    }

    [Fact]
    public void Title_ascending_is_case_insensitive()
    {
        var input = new[] { T(1, "banana"), T(2, "Apple"), T(3, "cherry") };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Title, SortDirection.Ascending).ToList();

        Assert.Equal(new[] { "Apple", "banana", "cherry" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Title_descending_reverses_order()
    {
        var input = new[] { T(1, "banana"), T(2, "Apple"), T(3, "cherry") };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Title, SortDirection.Descending).ToList();

        Assert.Equal(new[] { "cherry", "banana", "Apple" }, result.Select(t => t.Title));
    }

    [Fact]
    public void Priority_descending_puts_Critical_first()
    {
        var input = new[]
        {
            T(1, priority: TicketPriority.Idea),
            T(2, priority: TicketPriority.Critical),
            T(3, priority: TicketPriority.NiceToHave),
            T(4, priority: TicketPriority.Required),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Priority, SortDirection.Descending).ToList();

        Assert.Equal(
            new[] { TicketPriority.Critical, TicketPriority.Required, TicketPriority.NiceToHave, TicketPriority.Idea },
            result.Select(t => t.Priority));
    }

    [Fact]
    public void Priority_ascending_puts_Idea_first()
    {
        var input = new[]
        {
            T(1, priority: TicketPriority.Critical),
            T(2, priority: TicketPriority.Idea),
            T(3, priority: TicketPriority.Required),
            T(4, priority: TicketPriority.NiceToHave),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Priority, SortDirection.Ascending).ToList();

        Assert.Equal(
            new[] { TicketPriority.Idea, TicketPriority.NiceToHave, TicketPriority.Required, TicketPriority.Critical },
            result.Select(t => t.Priority));
    }

    [Fact]
    public void Assignee_ascending_places_nulls_last()
    {
        var input = new[]
        {
            T(1, assignedTo: "zoe"),
            T(2, assignedTo: null),
            T(3, assignedTo: "alice"),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Assignee, SortDirection.Ascending).ToList();

        Assert.Equal(new string?[] { "alice", "zoe", null }, result.Select(t => t.AssignedTo));
    }

    [Fact]
    public void Assignee_descending_places_nulls_last()
    {
        var input = new[]
        {
            T(1, assignedTo: "zoe"),
            T(2, assignedTo: null),
            T(3, assignedTo: "alice"),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.Assignee, SortDirection.Descending).ToList();

        Assert.Equal(new string?[] { "zoe", "alice", null }, result.Select(t => t.AssignedTo));
    }

    [Fact]
    public void CreatedAt_descending_returns_newest_first()
    {
        var input = new[]
        {
            T(1, createdAt: new DateTime(2025, 1, 1)),
            T(2, createdAt: new DateTime(2026, 6, 1)),
            T(3, createdAt: new DateTime(2025, 12, 1)),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.CreatedAt, SortDirection.Descending).ToList();

        Assert.Equal(new[] { 2, 3, 1 }, result.Select(t => t.Id));
    }

    [Fact]
    public void DueDate_ascending_places_nulls_last()
    {
        var input = new[]
        {
            T(1, dueDate: new DateTime(2026, 1, 1)),
            T(2, dueDate: null),
            T(3, dueDate: new DateTime(2025, 6, 1)),
        };

        var result = BoardSortState.ApplySort(input, ColumnSortMode.DueDate, SortDirection.Ascending).ToList();

        Assert.Equal(new[] { 3, 1, 2 }, result.Select(t => t.Id));
    }

    [Fact]
    public void Title_asc_then_desc_yields_reversed_result()
    {
        var input = new[] { T(1, "banana"), T(2, "apple"), T(3, "cherry") };

        var asc = BoardSortState.ApplySort(input, ColumnSortMode.Title, SortDirection.Ascending).ToList();
        var desc = BoardSortState.ApplySort(input, ColumnSortMode.Title, SortDirection.Descending).ToList();

        Assert.Equal(asc.AsEnumerable().Reverse(), desc);
    }

    [Theory]
    [InlineData(ColumnSortMode.Manual, SortDirection.Ascending)]
    [InlineData(ColumnSortMode.Title, SortDirection.Ascending)]
    [InlineData(ColumnSortMode.Priority, SortDirection.Descending)]
    [InlineData(ColumnSortMode.Assignee, SortDirection.Ascending)]
    [InlineData(ColumnSortMode.CreatedAt, SortDirection.Descending)]
    [InlineData(ColumnSortMode.DueDate, SortDirection.Ascending)]
    public void Empty_input_returns_empty(ColumnSortMode mode, SortDirection direction)
    {
        var result = BoardSortState.ApplySort(Array.Empty<TicketSummary>(), mode, direction);

        Assert.Empty(result);
    }
}
