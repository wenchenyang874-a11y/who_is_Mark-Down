namespace WhoIsMarkdown.Core.Markdown;

public interface IMarkdownRenderer
{
    public string RenderBody(
        string markdown,
        string? documentPath = null,
        RemoteImagePolicy? remoteImagePolicy = null);
}
