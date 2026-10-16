using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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
    private readonly SanitizedBlockCache blockCache = new();
    private readonly object renderGate = new();

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
        => RenderDocument(markdown, documentPath, remoteImagePolicy).BodyHtml;

    public MarkdownRenderResult RenderDocument(
        string markdown,
        string? documentPath = null,
        RemoteImagePolicy? remoteImagePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (renderGate)
        {
            // Always parse the complete document: reference links, abbreviations,
            // footnotes and heading identifiers can depend on later source blocks.
            MarkdownDocument document = Markdig.Markdown.Parse(markdown, pipeline);
            if (document.Descendants().Any(IsAuthorHtml))
            {
                // HTML may open in one Markdown block and close in another. Never
                // sanitize such fragments independently or infer balance with regex.
                string html = Markdig.Markdown.ToHtml(document, pipeline);
                return MarkdownRenderResult.Create([Rewrite(htmlSanitizer.Sanitize(html))]);
            }

            using StringWriter writer = new(System.Globalization.CultureInfo.InvariantCulture);
            HtmlRenderer renderer = new(writer);
            pipeline.Setup(renderer);
            List<string> safeBlocks = [];
            // Dense documents can contain tens of thousands of tiny top-level
            // blocks. Batch complete blocks to bound parser setup, cache entries
            // and DOM wrappers; never cut inside a list, table, fence or container.
            int blocksPerFragment = document.Count > 256 ? 32 : 1;
            int pendingBlocks = 0;
            foreach (Block block in document)
            {
                // Reuse one renderer so extension state and output ordering match
                // a full-document render, but cache only complete top-level blocks.
                renderer.Render(block);
                if (++pendingBlocks == blocksPerFragment)
                {
                    FlushFragment();
                }
            }

            if (pendingBlocks > 0) FlushFragment();

            return MarkdownRenderResult.Create(safeBlocks);

            void FlushFragment()
            {
                string generated = writer.ToString();
                writer.GetStringBuilder().Clear();
                pendingBlocks = 0;
                if (!string.IsNullOrWhiteSpace(generated))
                {
                    safeBlocks.Add(Rewrite(blockCache.GetOrAdd(generated, htmlSanitizer.Sanitize)));
                }
            }
        }

        string Rewrite(string html) => imageUrlResolver.RewriteGeneratedHtml(html, documentPath, remoteImagePolicy);
    }

    private static bool IsAuthorHtml(MarkdownObject node)
    {
        if (node is HtmlBlock) return true;
        if (node is not HtmlInline inline) return false;
        // UsePragmaLines inserts a synthetic HtmlInline with an empty source span.
        // Author-written lookalike anchors have real spans and must still take the
        // full-document HTML path. Never exempt arbitrary span-less extension HTML.
        const string prefix = "<a id=\"pragma-line-";
        const string suffix = "\"></a>";
        string tag = inline.Tag;
        bool generatedAnchor = inline.Span.End < inline.Span.Start
            && tag.StartsWith(prefix, StringComparison.Ordinal)
            && tag.EndsWith(suffix, StringComparison.Ordinal)
            && tag.Length > prefix.Length + suffix.Length
            && tag.AsSpan(prefix.Length, tag.Length - prefix.Length - suffix.Length).IndexOfAnyExcept("0123456789".AsSpan()) < 0;
        return !generatedAnchor;
    }
}
