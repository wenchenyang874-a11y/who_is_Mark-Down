using Markdig;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Converts Markdown into a safe HTML fragment. Raw HTML is disabled, source-line
/// anchors support editor/preview synchronization, and image URLs pass through a
/// document-root-constrained resolver.
/// </summary>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline pipeline;
    private readonly ILocalImageUrlResolver imageUrlResolver;

    public MarkdownRenderer()
        : this(new LocalImageUrlResolver())
    {
    }

    public MarkdownRenderer(ILocalImageUrlResolver imageUrlResolver)
    {
        this.imageUrlResolver = imageUrlResolver
            ?? throw new ArgumentNullException(nameof(imageUrlResolver));
        pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePragmaLines()
            .DisableHtml()
            .Build();
    }

    public string RenderBody(string markdown, string? documentPath = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string bodyHtml = Markdig.Markdown.ToHtml(markdown, pipeline);
        return imageUrlResolver.RewriteGeneratedHtml(bodyHtml, documentPath);
    }
}
