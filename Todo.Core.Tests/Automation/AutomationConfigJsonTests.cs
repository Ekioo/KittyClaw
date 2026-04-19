using System.Text.Json;

namespace Todo.Core.Tests.Automation;

public class AutomationConfigJsonTests
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    [Fact]
    public void RunAgentActionSpec_round_trip()
    {
        var spec = new RunAgentActionSpec { Agent = "programmer", MaxTurns = 50, ConcurrencyGroup = "code" };
        ActionSpec roundtrip = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        var a = Assert.IsType<RunAgentActionSpec>(roundtrip);
        Assert.Equal("programmer", a.Agent);
        Assert.Equal(50, a.MaxTurns);
        Assert.Equal("code", a.ConcurrencyGroup);
    }

    [Fact]
    public void MoveTicketStatusActionSpec_round_trip()
    {
        var spec = new MoveTicketStatusActionSpec { To = "InProgress" };
        ActionSpec r = JsonSerializer.Deserialize<ActionSpec>(JsonSerializer.Serialize<ActionSpec>(spec, Opts), Opts)!;
        Assert.Equal("InProgress", Assert.IsType<MoveTicketStatusActionSpec>(r).To);
    }

    [Fact]
    public void AllSubTicketsInStatusConditionSpec_round_trip()
    {
        var spec = new AllSubTicketsInStatusConditionSpec { Statuses = new() { "Done", "Review" } };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        var c = Assert.IsType<AllSubTicketsInStatusConditionSpec>(r);
        Assert.Equal(new[] { "Done", "Review" }, c.Statuses);
    }

    [Fact]
    public void TicketCountInColumnConditionSpec_round_trip()
    {
        var spec = new TicketCountInColumnConditionSpec
        {
            Columns = new() { "InProgress" },
            SameAssignee = true,
            Operator = "==",
            Value = 0
        };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        var c = Assert.IsType<TicketCountInColumnConditionSpec>(r);
        Assert.True(c.SameAssignee);
        Assert.Equal("==", c.Operator);
        Assert.Equal(0, c.Value);
    }

    [Fact]
    public void StatusChangeTriggerSpec_round_trip()
    {
        var spec = new StatusChangeTriggerSpec { From = "Review", To = "Done", PollSeconds = 15 };
        TriggerSpec r = JsonSerializer.Deserialize<TriggerSpec>(JsonSerializer.Serialize<TriggerSpec>(spec, Opts), Opts)!;
        var t = Assert.IsType<StatusChangeTriggerSpec>(r);
        Assert.Equal("Review", t.From);
        Assert.Equal("Done", t.To);
        Assert.Equal(15, t.PollSeconds);
    }

    [Fact]
    public void TicketInColumnTriggerSpec_round_trip()
    {
        var spec = new TicketInColumnTriggerSpec { Columns = new() { "Todo" }, AssigneeSlug = "programmer", Seconds = 30, DebounceSeconds = 5 };
        TriggerSpec r = JsonSerializer.Deserialize<TriggerSpec>(JsonSerializer.Serialize<TriggerSpec>(spec, Opts), Opts)!;
        var t = Assert.IsType<TicketInColumnTriggerSpec>(r);
        Assert.Equal(new[] { "Todo" }, t.Columns);
        Assert.Equal("programmer", t.AssigneeSlug);
        Assert.Equal(30, t.Seconds);
        Assert.Equal(5, t.DebounceSeconds);
    }

    [Fact]
    public void Condition_Negate_round_trip()
    {
        var spec = new TicketInColumnConditionSpec { Columns = new() { "Todo" }, Negate = true };
        ConditionSpec r = JsonSerializer.Deserialize<ConditionSpec>(JsonSerializer.Serialize<ConditionSpec>(spec, Opts), Opts)!;
        Assert.True(r.Negate);
    }
}
