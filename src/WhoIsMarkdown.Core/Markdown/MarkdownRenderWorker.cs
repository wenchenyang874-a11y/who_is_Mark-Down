namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Creates the renderer only on its first worker request and serializes complete
/// parses. Waiting requests cancel before materializing their immutable snapshots.
/// Active synchronous stages cannot be forcibly interrupted; cancellation is checked
/// between them. The shared queue also supplies the preview preparation lifecycle.
/// </summary>
public sealed class MarkdownRenderWorker : IDisposable
{
    private readonly BackgroundWorkQueue queue = new();
    private readonly Lazy<IMarkdownRenderer> renderer;

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
    public Task<MarkdownRenderResult> RenderSnapshotAsync(
        Func<string> readSnapshot,
        string? documentPath,
        RemoteImagePolicy? policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        return queue.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string markdown = readSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            return renderer.Value.RenderDocument(markdown, documentPath, policy, cancellationToken);
        }, cancellationToken);
    }

    public void Dispose() => queue.Dispose();
}
