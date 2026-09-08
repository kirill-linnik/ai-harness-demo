using System.Threading.Channels;

namespace AiHarnessDemo.Services;

public sealed class FlowQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private int _pendingCount;

    public ChannelReader<Guid> Reader => _channel.Reader;

    internal int PendingCount => Volatile.Read(ref _pendingCount);

    public bool Queue(Guid flowId)
    {
        Interlocked.Increment(ref _pendingCount);
        if (_channel.Writer.TryWrite(flowId))
        {
            return true;
        }

        Interlocked.Decrement(ref _pendingCount);
        return false;
    }

    internal void MarkDequeued()
    {
        if (Interlocked.Decrement(ref _pendingCount) < 0)
        {
            Interlocked.Exchange(ref _pendingCount, 0);
            throw new InvalidOperationException(
                "The flow queue dequeue count became inconsistent.");
        }
    }
}
