namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Admits one CPU-bound operation at a time. Queued requests cancel before creating
/// large strings; an executing synchronous operation checks its own stage boundaries.
/// Dispose rejects new work and releases the semaphore after existing waits unwind.
/// </summary>
internal sealed class BackgroundWorkQueue : IDisposable
{
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim workGate = new(1, 1);
    private int requests;
    private bool disposed;

    public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requests++;
        }

        try
        {
            await workGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                workGate.Release();
            }
        }
        finally
        {
            lock (lifecycleGate)
            {
                requests--;
                if (disposed && requests == 0) workGate.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            if (requests == 0) workGate.Dispose();
        }
    }
}
