namespace Todo.Core.Tests.Automation;

public class SignalTests
{
    [Fact]
    public void StatusChangeSignal_has_value_equality()
    {
        var a = new StatusChangeSignal(42, "Todo", "Done");
        var b = new StatusChangeSignal(42, "Todo", "Done");
        Assert.Equal(a, b);
    }

    [Fact]
    public void StatusChangeSignal_differs_on_any_field()
    {
        var a = new StatusChangeSignal(42, "Todo", "Done");
        Assert.NotEqual(a, new StatusChangeSignal(43, "Todo", "Done"));
        Assert.NotEqual(a, new StatusChangeSignal(42, "Review", "Done"));
        Assert.NotEqual(a, new StatusChangeSignal(42, "Todo", "Review"));
    }

    [Fact]
    public void CommentAddedSignal_has_value_equality()
    {
        var a = new CommentAddedSignal(1, "owner", "hi");
        var b = new CommentAddedSignal(1, "owner", "hi");
        Assert.Equal(a, b);
    }

    [Fact]
    public void TriggerFiring_is_a_record_with_nullable_fields()
    {
        var f1 = new TriggerFiring(null, null, null);
        var f2 = new TriggerFiring(null, null, null);
        Assert.Equal(f1, f2);

        var f3 = new TriggerFiring(42, "title", "Done");
        Assert.NotEqual(f1, f3);
    }
}
