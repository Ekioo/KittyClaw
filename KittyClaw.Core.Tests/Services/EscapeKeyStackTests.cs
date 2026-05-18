using KittyClaw.Web.Services;

// Failing tests for ticket #187: EscapeKeyStack contract (LIFO close-handler registry).
namespace KittyClaw.Core.Tests.Services;

public class EscapeKeyStackTests
{
    [Fact]
    public void HandleEscape_OnEmptyStack_ReturnsFalse()
    {
        var stack = new EscapeKeyStack();

        var handled = stack.HandleEscape();

        Assert.False(handled);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void Push_ThenHandleEscape_InvokesHandler_AndPopsIt()
    {
        var stack = new EscapeKeyStack();
        var calls = 0;
        stack.Push(() => calls++);

        var first = stack.HandleEscape();
        var second = stack.HandleEscape();

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, calls);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void Push_TwoHandlers_HandleEscape_InvokesTopmostFirst()
    {
        var stack = new EscapeKeyStack();
        var order = new List<string>();
        stack.Push(() => order.Add("A"));
        stack.Push(() => order.Add("B"));

        stack.HandleEscape();
        stack.HandleEscape();

        Assert.Equal(new[] { "B", "A" }, order);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void Dispose_Token_RemovesHandlerFromMiddleOfStack()
    {
        var stack = new EscapeKeyStack();
        var order = new List<string>();
        stack.Push(() => order.Add("A"));
        var tokenB = stack.Push(() => order.Add("B"));
        stack.Push(() => order.Add("C"));

        tokenB.Dispose();
        stack.HandleEscape();
        stack.HandleEscape();

        Assert.Equal(new[] { "C", "A" }, order);
        Assert.DoesNotContain("B", order);
    }

    [Fact]
    public void Dispose_TopToken_DoesNotInvokeHandler()
    {
        var stack = new EscapeKeyStack();
        var calls = 0;
        var token = stack.Push(() => calls++);

        token.Dispose();
        var handled = stack.HandleEscape();

        Assert.False(handled);
        Assert.Equal(0, calls);
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void Push_NullHandler_Throws()
    {
        var stack = new EscapeKeyStack();

        Action act = () => stack.Push(null!);
        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public void Dispose_SameTokenTwice_IsNoOp()
    {
        var stack = new EscapeKeyStack();
        var aCalls = 0;
        var bCalls = 0;
        stack.Push(() => aCalls++);
        var tokenB = stack.Push(() => bCalls++);

        tokenB.Dispose();
        tokenB.Dispose();
        stack.HandleEscape();

        Assert.Equal(1, aCalls);
        Assert.Equal(0, bCalls);
    }

    [Fact]
    public void HandleEscape_PopsBeforeInvoking_ThrowingHandlerDoesNotZombieStack()
    {
        var stack = new EscapeKeyStack();
        stack.Push(() => throw new InvalidOperationException("boom"));

        Action act = () => stack.HandleEscape();
        Assert.Throws<InvalidOperationException>(act);
        Assert.Equal(0, stack.Count);
        Assert.False(stack.HandleEscape());
    }
}
