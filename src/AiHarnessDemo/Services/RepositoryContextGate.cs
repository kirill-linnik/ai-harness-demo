namespace AiHarnessDemo.Services;

/// <summary>
/// Prevents repository analysis from changing shared context while an intake request snapshots it.
/// This local application favors simple exclusive access over a more complex reader/writer scheme.
/// </summary>
public sealed class RepositoryContextGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public Task<IDisposable> EnterReadAsync(
        CancellationToken cancellationToken = default) =>
        AcquireAsync(cancellationToken);

    public Task<IDisposable> EnterWriteAsync(
        CancellationToken cancellationToken = default) =>
        AcquireAsync(cancellationToken);

    private async Task<IDisposable> AcquireAsync(
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        return new Lease(_mutex);
    }

    private sealed class Lease(SemaphoreSlim mutex) : IDisposable
    {
        private SemaphoreSlim? _mutex = mutex;

        public void Dispose() =>
            Interlocked.Exchange(ref _mutex, null)?.Release();
    }
}
