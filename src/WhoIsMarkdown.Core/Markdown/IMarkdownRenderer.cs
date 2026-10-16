namespace WhoIsMarkdown.Core.Markdown;

public interface IMarkdownRenderer
{
    public MarkdownRenderResult RenderDocument(
        string markdown,
        string? documentPath,
        RemoteImagePolicy? remoteImagePolicy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkdownRenderResult result = RenderDocument(markdown, documentPath, remoteImagePolicy);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public MarkdownRenderResult RenderDocument(
        string markdown,
        string? documentPath = null,
        RemoteImagePolicy? remoteImagePolicy = null)
        => MarkdownRenderResult.Create([RenderBody(markdown, documentPath, remoteImagePolicy)]);

    public string RenderBody(
        string markdown,
        string? documentPath = null,
        RemoteImagePolicy? remoteImagePolicy = null);
}
