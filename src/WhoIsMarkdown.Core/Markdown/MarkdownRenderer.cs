using Markdig;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Converts Markdown into a safe HTML fragment. Raw HTML passes through WIMD's
/// explicit allowlist, source-line anchors support editor/preview synchronization,
/// and image URLs pass through a document-root-constrained resolver.
/// </summary>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline pipeline;
    private readonly ILocalImageUrlResolver imageUrlResolver;
    private readonly MarkdownHtmlSanitizer htmlSanitizer;

    public MarkdownRenderer()
        : this(new LocalImageUrlResolver(), new MarkdownHtmlSanitizer())
    {
    }

    public MarkdownRenderer(ILocalImageUrlResolver imageUrlResolver)
        : this(imageUrlResolver, new MarkdownHtmlSanitizer())
    {
    }

    public MarkdownRenderer(
        ILocalImageUrlResolver imageUrlResolver,
        MarkdownHtmlSanitizer htmlSanitizer)
    {
        this.imageUrlResolver = imageUrlResolver
            ?? throw new ArgumentNullException(nameof(imageUrlResolver));
        this.htmlSanitizer = htmlSanitizer
            ?? throw new ArgumentNullException(nameof(htmlSanitizer));
        pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UsePragmaLines()
            .Build();
    }

    public string RenderBody(
        string markdown,
        string? documentPath = null,
        RemoteImagePolicy? remoteImagePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string generatedHtml = Markdig.Markdown.ToHtml(markdown, pipeline);
        string safeHtml = htmlSanitizer.Sanitize(generatedHtml);
        return imageUrlResolver.RewriteGeneratedHtml(safeHtml, documentPath, remoteImagePolicy);
    }
}
