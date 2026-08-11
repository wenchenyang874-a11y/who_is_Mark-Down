using Markdig;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Converts the supported CommonMark/GFM-style syntax to an HTML fragment.
/// Raw HTML is disabled at the parser boundary so document content cannot inject
/// script elements, event attributes, frames, or active embedded objects.
/// </summary>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline pipeline;

    public MarkdownRenderer()
    {
        pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
    }

    public string RenderBody(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Markdig.Markdown.ToHtml(markdown, pipeline);
    }
}
