using WhoIsMarkdown.Core.Diagnostics;

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

    /// <summary>
    /// Bug fix: the first preview paid renderer construction only after Chromium
    /// was ready. Admit preparation through the existing background queue so the
    /// browser and renderer can initialize concurrently without parsing a dummy
    /// document, racing a render, or keeping a UI object alive after close.
    /// Cancellation stops queued work; an entered synchronous factory may finish.
    /// </summary>
    public Task PrepareAsync(CancellationToken cancellationToken)
        => queue.RunAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetRenderer();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }, cancellationToken);

    private IMarkdownRenderer GetRenderer()
        => renderer.IsValueCreated ? renderer.Value
            : CoreOperationTrace.Measure("RendererCreate", () => renderer.Value);

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
            string markdown = CoreOperationTrace.Measure("RenderSnapshot", readSnapshot);
            cancellationToken.ThrowIfCancellationRequested();
            IMarkdownRenderer current = GetRenderer();
            // The parent request awaits on the UI thread; this scope names the
            // actual worker, including batched render/filter stages and locks.
            return CoreOperationTrace.Measure("RenderExecution",
                () => current.RenderDocument(markdown, documentPath, policy, cancellationToken));
        }, cancellationToken);
    }

    public void Dispose() => queue.Dispose();
}
