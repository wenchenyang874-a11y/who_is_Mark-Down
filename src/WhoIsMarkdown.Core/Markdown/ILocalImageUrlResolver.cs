namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Rewrites image sources in trusted renderer output without granting the preview
/// arbitrary file-system or network access.
/// </summary>
public interface ILocalImageUrlResolver
{
    public string RewriteGeneratedHtml(
        string bodyHtml,
        string? documentPath,
        RemoteImagePolicy? remoteImagePolicy = null);
}
