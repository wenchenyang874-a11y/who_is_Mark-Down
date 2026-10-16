namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Creates the renderer only on its first worker request and serializes complete
/// parses. Cancellation used to stop only debounce timers: obsolete parses could
/// still pile up behind the sanitizer lock, retaining multiple document snapshots.
/// Waiting requests now cancel before parsing. An active parse is not interruptible;
/// disposal releases its gate only after all outstanding requests have unwound.
/// </summary>
public sealed class MarkdownRenderWorker : IDisposable
{
    private readonly object lifecycleGate = new();
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private readonly Lazy<IMarkdownRenderer> renderer;
    private int requests;
    private bool disposed;

    public MarkdownRenderWorker(Func<IMarkdownRenderer>? factory = null)
    {
        renderer = new Lazy<IMarkdownRenderer>(factory ?? (() => new MarkdownRenderer()));
    }

    public async Task<string> RenderAsync(
        string markdown,
        string? documentPath,
        RemoteImagePolicy? policy,
        CancellationToken cancellationToken)
        => (await RenderDocumentAsync(markdown, documentPath, policy, cancellationToken).ConfigureAwait(false)).BodyHtml;

    public Task<MarkdownRenderResult> RenderDocumentAsync(
        string markdown,
        string? documentPath,
        RemoteImagePolicy? policy,
        CancellationToken cancellationToken)
        => RenderSnapshotAsync(() => markdown, documentPath, policy, cancellationToken);

    /// <summary>
    /// Materializes an immutable snapshot only after debounce and queue admission.
    /// The factory must capture data, never a UI control or a mutable document.
    /// Canceled queued requests do not allocate full document strings.
    /// </summary>
    public async Task<MarkdownRenderResult> RenderSnapshotAsync(
        Func<string> readSnapshot,
        string? documentPath,
        RemoteImagePolicy? policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requests++;
        }

        try
        {
            await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string markdown = readSnapshot();
                        cancellationToken.ThrowIfCancellationRequested();
                        return renderer.Value.RenderDocument(markdown, documentPath, policy);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                renderGate.Release();
            }
        }
        finally
        {
            lock (lifecycleGate)
            {
                requests--;
                if (disposed && requests == 0)
                {
                    renderGate.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (requests == 0)
            {
                renderGate.Dispose();
            }
        }
    }
}
