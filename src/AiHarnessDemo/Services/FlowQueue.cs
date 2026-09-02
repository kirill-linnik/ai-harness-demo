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

    public ChannelReader<Guid> Reader => _channel.Reader;

    public bool Queue(Guid flowId) => _channel.Writer.TryWrite(flowId);
}
