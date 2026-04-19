namespace Todo.Core.Tests.Automation;

public class StatusChangeTriggerTests
{
    [Fact]
    public void TryHandleExternalSignal_ignores_non_status_signals()
    {
        var t = new StatusChangeTrigger(new StatusChangeTriggerSpec());
        var handled = t.TryHandleExternalSignal(new object(), out var firings);
        Assert.False(handled);
        Assert.Empty(firings);
    }

    [Fact]
    public void TryHandleExternalSignal_no_filter_matches_any_transition()
    {
        var t = new StatusChangeTrigger(new StatusChangeTriggerSpec());
        var handled = t.TryHandleExternalSignal(new StatusChangeSignal(42, "Todo", "Done"), out var firings);
        Assert.True(handled);
        var f = Assert.Single(firings);
        Assert.Equal(42, f.TicketId);
        Assert.Equal("Done", f.TicketStatus);
    }

    [Fact]
    public void TryHandleExternalSignal_filters_on_to_column()
    {
        var spec = new StatusChangeTriggerSpec { To = "Done" };
        var t = new StatusChangeTrigger(spec);

        Assert.True(t.TryHandleExternalSignal(new StatusChangeSignal(1, "Review", "Done"), out var a));
        Assert.Single(a);

        Assert.False(t.TryHandleExternalSignal(new StatusChangeSignal(2, "Review", "Todo"), out var b));
        Assert.Empty(b);
    }

    [Fact]
    public void TryHandleExternalSignal_filters_on_from_column()
    {
        var spec = new StatusChangeTriggerSpec { From = "InProgress", To = "Review" };
        var t = new StatusChangeTrigger(spec);

        Assert.True(t.TryHandleExternalSignal(new StatusChangeSignal(1, "InProgress", "Review"), out var a));
        Assert.Single(a);

        Assert.False(t.TryHandleExternalSignal(new StatusChangeSignal(2, "Todo", "Review"), out var b));
        Assert.Empty(b);
    }

    [Fact]
    public void TryHandleExternalSignal_produces_firing_with_new_status()
    {
        var t = new StatusChangeTrigger(new StatusChangeTriggerSpec { To = "Done" });
        t.TryHandleExternalSignal(new StatusChangeSignal(7, "Review", "Done"), out var firings);
        var firing = Assert.Single(firings);
        Assert.Equal("Done", firing.TicketStatus);
        Assert.Equal(7, firing.TicketId);
    }
}
