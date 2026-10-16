namespace WhoIsMarkdown.Core.Markdown;

/// <summary>Serializes pure-data preview packaging without retaining UI objects.</summary>
public sealed class PreviewPreparationWorker(IPreviewContentBuilder? builder = null) : IDisposable
{
    private readonly IPreviewContentBuilder builder = builder ?? new PreviewContentBuilder();
    private readonly BackgroundWorkQueue queue = new();

    public Task<PreparedPreviewContent> PrepareAsync(
        PreviewContent content,
        IReadOnlyList<MarkdownRenderBlock>? previousBlocks,
        bool navigate,
        bool forceBodyReplacement,
        CancellationToken cancellationToken)
        => queue.RunAsync(() => builder.Prepare(content, previousBlocks, navigate, forceBodyReplacement, cancellationToken), cancellationToken);

    public Task<string> BuildReplacementAsync(PreviewContent content, CancellationToken cancellationToken)
        => queue.RunAsync(() => builder.BuildReplacement(content, cancellationToken), cancellationToken);

    public void Dispose() => queue.Dispose();
}
