using KittyClaw.Core.Automation;
using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public sealed class RunEventStreamBufferTests
{
    [Fact]
    public void Full_subscriber_is_disconnected_once_and_cannot_keep_growing()
    {
        var disconnects = 0;
        var queue = new RunEventStreamBuffer(() => disconnects++);
        for (var i = 0; i < RunEventStreamBuffer.Capacity + 1000; i++)
            queue.Add(new StreamEvent(DateTime.UtcNow, "assistant", i.ToString()));
        Assert.Equal(1, disconnects);
        for (var i = 0; i < RunEventStreamBuffer.Capacity; i++)
        {
            Assert.True(queue.Reader.TryRead(out var item));
            Assert.Equal(i.ToString(), item.Text);
        }
        Assert.False(queue.Reader.TryRead(out _));
        queue.Add(new StreamEvent(DateTime.UtcNow, "assistant", "after disconnect"));
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Equal(1, disconnects);
    }

    [Fact]
    public void Healthy_subscriber_receives_events_in_order_without_disconnect()
    {
        var disconnects = 0;
        var queue = new RunEventStreamBuffer(() => disconnects++);
        for (var i = 0; i < 1000; i++)
        {
            var item = new StreamEvent(DateTime.UtcNow, "tool_use", i.ToString());
            queue.Add(item);
            Assert.True(queue.Reader.TryRead(out var received));
            Assert.Same(item, received);
        }
        Assert.Equal(0, disconnects);
    }

    [Fact]
    public void Slow_subscriber_does_not_block_a_healthy_subscriber()
    {
        var disconnects = 0;
        var slow = new RunEventStreamBuffer(() => Interlocked.Increment(ref disconnects));
        var healthy = new RunEventStreamBuffer(() => Assert.Fail("Healthy client disconnected"));
        for (var i = 0; i < 1000; i++)
        {
            var item = new StreamEvent(DateTime.UtcNow, "assistant", i.ToString());
            slow.Add(item);
            healthy.Add(item);
            Assert.True(healthy.Reader.TryRead(out _));
        }
        Assert.Equal(1, disconnects);
    }
}
