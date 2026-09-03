using System.Collections.Concurrent;

namespace AiHarnessDemo.Services;

public sealed class FlowLifecycleCoordinator
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async ValueTask<IAsyncDisposable> EnterAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(flowId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
