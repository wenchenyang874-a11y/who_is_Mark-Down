namespace WhoIsMarkdown.Core.Markdown;

/// <summary>Immutable input for background-only preview packaging.</summary>
public sealed record PreviewContent(
    string BodyHtml,
    IReadOnlyList<MarkdownRenderBlock>? Blocks,
    string StyleSheet,
    string AppearanceStyleSheet,
    RemoteImagePolicy ImagePolicy,
    string? PrebuiltPage = null);

/// <summary>
/// Exactly one delivery form is prepared: a navigation page or a body script.
/// Large fallback strings are not allocated until a browser patch actually fails.
/// </summary>
public sealed record PreparedPreviewContent(
    string? NavigationHtml,
    string? UpdateScript,
    bool IsPatch,
    bool ContainsMermaid,
    IReadOnlyList<MarkdownRenderBlock>? Blocks);

public interface IPreviewContentBuilder
{
    public PreparedPreviewContent Prepare(
        PreviewContent content,
        IReadOnlyList<MarkdownRenderBlock>? previousBlocks,
        bool navigate,
        bool forceBodyReplacement,
        CancellationToken cancellationToken);

    public string BuildReplacement(PreviewContent content, CancellationToken cancellationToken);
}

/// <summary>
/// Pure-data preparation, invoked off the dispatcher by the preview queue. Full
/// pages used to be assembled for every keystroke, followed by JSON serialization
/// on the UI thread. Prepare only the required form and check cancellation between
/// each non-interruptible packaging stage; never access controls from this class.
/// </summary>
public sealed class PreviewContentBuilder(IPreviewDocumentBuilder? documentBuilder = null) : IPreviewContentBuilder
{
    private readonly IPreviewDocumentBuilder documentBuilder = documentBuilder ?? new PreviewDocumentBuilder();

    public PreparedPreviewContent Prepare(
        PreviewContent content,
        IReadOnlyList<MarkdownRenderBlock>? previousBlocks,
        bool navigate,
        bool forceBodyReplacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        bool containsMermaid = content.BodyHtml.Contains("language-mermaid", StringComparison.Ordinal)
            || content.BodyHtml.Contains("class=\"mermaid\"", StringComparison.Ordinal);
        // Empty previews contain a host-owned placeholder, not semantic blocks.
        // Treating that placeholder as an empty block list would erase it on patch.
        IReadOnlyList<MarkdownRenderBlock>? blocks = content.Blocks is { Count: > 0 } ? content.Blocks : null;
        string? page = null;
        string? script = null;
        bool patch = !navigate && !forceBodyReplacement && previousBlocks is not null && blocks is not null;
        cancellationToken.ThrowIfCancellationRequested();
        if (navigate)
        {
            page = content.PrebuiltPage ?? documentBuilder.Build(
                content.BodyHtml,
                string.Concat(content.StyleSheet, Environment.NewLine, content.AppearanceStyleSheet),
                content.ImagePolicy);
        }
        else
        {
            script = patch
                ? PreviewUpdateScriptBuilder.BuildPatch(previousBlocks!, blocks!, cancellationToken)
                : BuildReplacement(content, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedPreviewContent(page, script, patch, containsMermaid, blocks);
    }

    public string BuildReplacement(PreviewContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        string script = PreviewUpdateScriptBuilder.Build(documentBuilder.GetVisibleBody(content.BodyHtml));
        cancellationToken.ThrowIfCancellationRequested();
        return script;
    }
}
