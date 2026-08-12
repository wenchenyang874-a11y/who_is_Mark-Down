namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Builds a complete preview document from rendered Markdown and trusted application styles.
/// Keeping this boundary injectable lets the desktop host evolve independently from rendering.
/// </summary>
public interface IPreviewDocumentBuilder
{
    public string Build(string bodyHtml, string styleSheet);

    public string GetVisibleBody(string bodyHtml);
}
