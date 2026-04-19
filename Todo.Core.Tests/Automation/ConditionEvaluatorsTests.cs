namespace Todo.Core.Tests.Automation;

public class ConditionEvaluatorsTests
{
    // ───── TicketInColumn ─────

    [Fact]
    public void TicketInColumn_matches_when_status_is_listed()
    {
        var c = new TicketInColumnConditionSpec { Columns = new() { "Todo", "InProgress" } };
        Assert.True(ConditionEvaluators.TicketInColumn(c, "InProgress"));
    }

    [Fact]
    public void TicketInColumn_rejects_when_status_not_listed()
    {
        var c = new TicketInColumnConditionSpec { Columns = new() { "Todo" } };
        Assert.False(ConditionEvaluators.TicketInColumn(c, "Done"));
    }

    [Fact]
    public void TicketInColumn_empty_columns_matches_any_non_null_status()
    {
        var c = new TicketInColumnConditionSpec { Columns = new() };
        Assert.True(ConditionEvaluators.TicketInColumn(c, "Anything"));
    }

    [Fact]
    public void TicketInColumn_null_status_never_matches()
    {
        var c = new TicketInColumnConditionSpec { Columns = new() };
        Assert.False(ConditionEvaluators.TicketInColumn(c, null));
    }

    // ───── MinDescriptionLength ─────

    [Fact]
    public void MinDescriptionLength_passes_when_above_threshold()
    {
        var c = new MinDescriptionLengthConditionSpec { Length = 10 };
        Assert.True(ConditionEvaluators.MinDescriptionLength(c, "lorem ipsum dolor"));
    }

    [Fact]
    public void MinDescriptionLength_fails_when_below_threshold()
    {
        var c = new MinDescriptionLengthConditionSpec { Length = 50 };
        Assert.False(ConditionEvaluators.MinDescriptionLength(c, "short"));
    }

    [Fact]
    public void MinDescriptionLength_null_description_treated_as_empty()
    {
        var c = new MinDescriptionLengthConditionSpec { Length = 1 };
        Assert.False(ConditionEvaluators.MinDescriptionLength(c, null));
    }

    // ───── FieldLength ─────

    [Theory]
    [InlineData("title", "min", 5, "longtitle", null, true)]
    [InlineData("title", "min", 10, "short", null, false)]
    [InlineData("title", "max", 5, "short", null, true)]
    [InlineData("title", "max", 3, "toolong", null, false)]
    [InlineData("description", "min", 5, null, "hello world", true)]
    [InlineData("description", "max", 5, null, "hello world", false)]
    public void FieldLength_boundary_cases(string field, string mode, int length, string? title, string? desc, bool expected)
    {
        var c = new FieldLengthConditionSpec { Field = field, Mode = mode, Length = length };
        Assert.Equal(expected, ConditionEvaluators.FieldLength(c, title, desc));
    }

    // ───── Priority ─────

    [Fact]
    public void Priority_matches_when_in_list()
    {
        var c = new PriorityConditionSpec { Priorities = new() { "Required", "Critical" } };
        Assert.True(ConditionEvaluators.Priority(c, TicketPriority.Required));
    }

    [Fact]
    public void Priority_rejects_when_not_in_list()
    {
        var c = new PriorityConditionSpec { Priorities = new() { "Critical" } };
        Assert.False(ConditionEvaluators.Priority(c, TicketPriority.NiceToHave));
    }

    [Fact]
    public void Priority_empty_list_always_matches()
    {
        var c = new PriorityConditionSpec { Priorities = new() };
        Assert.True(ConditionEvaluators.Priority(c, TicketPriority.Idea));
    }

    // ───── Labels ─────

    [Fact]
    public void Labels_matches_when_ticket_has_one_of_required_labels()
    {
        var c = new LabelsConditionSpec { Labels = new() { "bug", "urgent" } };
        Assert.True(ConditionEvaluators.Labels(c, new[] { "cosmetic", "urgent" }));
    }

    [Fact]
    public void Labels_rejects_when_ticket_has_no_required_label()
    {
        var c = new LabelsConditionSpec { Labels = new() { "bug" } };
        Assert.False(ConditionEvaluators.Labels(c, new[] { "feature" }));
    }

    [Fact]
    public void Labels_empty_list_always_matches()
    {
        var c = new LabelsConditionSpec { Labels = new() };
        Assert.True(ConditionEvaluators.Labels(c, Array.Empty<string>()));
    }

    // ───── AssignedTo ─────

    [Fact]
    public void AssignedTo_empty_slugs_requires_unassigned()
    {
        var c = new AssignedToConditionSpec { Slugs = new() };
        Assert.True(ConditionEvaluators.AssignedTo(c, null));
        Assert.False(ConditionEvaluators.AssignedTo(c, "programmer"));
    }

    [Fact]
    public void AssignedTo_matches_when_slug_listed()
    {
        var c = new AssignedToConditionSpec { Slugs = new() { "programmer", "producer" } };
        Assert.True(ConditionEvaluators.AssignedTo(c, "producer"));
        Assert.False(ConditionEvaluators.AssignedTo(c, "groomer"));
    }

    // ───── HasParent ─────

    [Fact]
    public void HasParent_true_requires_non_null_parent()
    {
        var c = new HasParentConditionSpec { Value = true };
        Assert.True(ConditionEvaluators.HasParent(c, 42));
        Assert.False(ConditionEvaluators.HasParent(c, null));
    }

    [Fact]
    public void HasParent_false_requires_null_parent()
    {
        var c = new HasParentConditionSpec { Value = false };
        Assert.True(ConditionEvaluators.HasParent(c, null));
        Assert.False(ConditionEvaluators.HasParent(c, 42));
    }

    // ───── AllSubTicketsInStatus ─────

    [Fact]
    public void AllSubTicketsInStatus_matches_when_all_subs_in_allowed_statuses()
    {
        var c = new AllSubTicketsInStatusConditionSpec { Statuses = new() { "Done", "Review" } };
        var subs = new[]
        {
            new SubTicketInfo(1, "a", "Done", null),
            new SubTicketInfo(2, "b", "Review", null),
        };
        Assert.True(ConditionEvaluators.AllSubTicketsInStatus(c, subs));
    }

    [Fact]
    public void AllSubTicketsInStatus_rejects_when_one_sub_is_outside()
    {
        var c = new AllSubTicketsInStatusConditionSpec { Statuses = new() { "Done" } };
        var subs = new[]
        {
            new SubTicketInfo(1, "a", "Done", null),
            new SubTicketInfo(2, "b", "InProgress", null),
        };
        Assert.False(ConditionEvaluators.AllSubTicketsInStatus(c, subs));
    }

    [Fact]
    public void AllSubTicketsInStatus_rejects_empty_sub_list()
    {
        var c = new AllSubTicketsInStatusConditionSpec { Statuses = new() { "Done" } };
        Assert.False(ConditionEvaluators.AllSubTicketsInStatus(c, Array.Empty<SubTicketInfo>()));
    }

    // ───── TicketAge ─────

    [Fact]
    public void TicketAge_olderThan_matches_when_age_exceeds_threshold()
    {
        var c = new TicketAgeConditionSpec { Field = "createdAt", Mode = "olderThan", Hours = 24 };
        var now = new DateTime(2026, 04, 19, 12, 0, 0, DateTimeKind.Utc);
        var created = now.AddHours(-25);
        Assert.True(ConditionEvaluators.TicketAge(c, created, now, now));
    }

    [Fact]
    public void TicketAge_newerThan_matches_when_age_below_threshold()
    {
        var c = new TicketAgeConditionSpec { Field = "updatedAt", Mode = "newerThan", Hours = 1 };
        var now = new DateTime(2026, 04, 19, 12, 0, 0, DateTimeKind.Utc);
        var updated = now.AddMinutes(-30);
        Assert.True(ConditionEvaluators.TicketAge(c, now.AddDays(-10), updated, now));
    }

    [Fact]
    public void TicketAge_boundary_olderThan_equal_hours_matches()
    {
        var c = new TicketAgeConditionSpec { Field = "createdAt", Mode = "olderThan", Hours = 24 };
        var now = new DateTime(2026, 04, 19, 12, 0, 0, DateTimeKind.Utc);
        var created = now.AddHours(-24);
        Assert.True(ConditionEvaluators.TicketAge(c, created, now, now));
    }

    // ───── CompareCount ─────

    [Theory]
    [InlineData("==", 3, 3, true)]
    [InlineData("==", 3, 4, false)]
    [InlineData("!=", 3, 4, true)]
    [InlineData("<",  2, 3, true)]
    [InlineData("<",  3, 3, false)]
    [InlineData("<=", 3, 3, true)]
    [InlineData(">",  4, 3, true)]
    [InlineData(">=", 3, 3, true)]
    [InlineData("??", 3, 3, false)]
    public void CompareCount_operators(string op, int count, int value, bool expected)
    {
        Assert.Equal(expected, ConditionEvaluators.CompareCount(op, count, value));
    }

    // ───── ResolveAgentPlaceholder ─────

    [Fact]
    public void ResolveAgentPlaceholder_no_placeholder_returns_input()
    {
        Assert.Equal("programmer", ConditionEvaluators.ResolveAgentPlaceholder("programmer", "anyone"));
    }

    [Fact]
    public void ResolveAgentPlaceholder_substitutes_placeholder()
    {
        Assert.Equal("qa-tester", ConditionEvaluators.ResolveAgentPlaceholder("{assignee}", "qa-tester"));
    }

    [Fact]
    public void ResolveAgentPlaceholder_returns_null_when_assignee_missing()
    {
        Assert.Null(ConditionEvaluators.ResolveAgentPlaceholder("{assignee}", null));
        Assert.Null(ConditionEvaluators.ResolveAgentPlaceholder("{assignee}", ""));
    }
}
