using System.Threading.Channels;
using KittyClaw.Core.Automation;

namespace KittyClaw.Web.Services;

/// <summary>Bounds each SSE subscriber without blocking the shared agent event producer.</summary>
internal sealed class RunEventStreamBuffer(Action disconnect)
{
    internal const int Capacity = 256;
    private readonly Channel<StreamEvent> _queue = Channel.CreateBounded<StreamEvent>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private int _disconnected;

    public ChannelReader<StreamEvent> Reader => _queue.Reader;

    public void Add(StreamEvent item)
    {
        if (_queue.Writer.TryWrite(item)) return;
        // A slow client must reconnect; dropping events silently would hide run evidence.
        _queue.Writer.TryComplete();
        if (Interlocked.Exchange(ref _disconnected, 1) == 0) disconnect();
    }
}
