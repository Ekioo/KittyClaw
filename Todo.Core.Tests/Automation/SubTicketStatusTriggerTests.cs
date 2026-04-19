namespace Todo.Core.Tests.Automation;

public class SubTicketStatusTriggerTests
{
    [Fact]
    public void TryHandleExternalSignal_ignores_non_status_signals()
    {
        var t = new SubTicketStatusTrigger(new SubTicketStatusTriggerSpec());
        var handled = t.TryHandleExternalSignal(new object(), out var firings);
        Assert.False(handled);
        Assert.Empty(firings);
    }

    [Fact]
    public void TryHandleExternalSignal_queues_status_signal_but_returns_false()
    {
        // The trigger cannot resolve the parent synchronously (needs the TicketService).
        // It must queue the child ID and return false; EvaluateAsync will drain later.
        var t = new SubTicketStatusTrigger(new SubTicketStatusTriggerSpec());
        var handled = t.TryHandleExternalSignal(new StatusChangeSignal(99, "Todo", "Done"), out var firings);
        Assert.False(handled);
        Assert.Empty(firings);
    }
}
