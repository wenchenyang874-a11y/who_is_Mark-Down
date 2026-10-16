namespace WhoIsMarkdown.Core.Markdown;

public interface IMarkdownRenderer
{
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
