using Markdig;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class MarkdownBlockRenderingTests
{
    [Theory]
    [InlineData("# heading\n\nparagraph **bold**\n\n---\n\nend")]
    [InlineData("- [ ] todo\n- [x] done\n  - nested\n\n> quote\n>\n> next")]
    [InlineData("| A | B |\n|---|---|\n| one | two |\n\n```csharp\nif (a < b) {}\n```\n")]
    [InlineData("[one][ref]\n\n## heading\n\n[ref]: https://example.com/first")]
    [InlineData("one[^a]\n\nsecond[^a]\n\n[^a]: footnote\n\n    continuation")]
    [InlineData("# same\n\n# same\n\n*[API]: interface\n\nAPI works")]
    [InlineData("term\n: definition\n\n::: note\ninside\n:::")]
    [InlineData("<div align=\"center\">\n\n# nested\n\n</div>\n\nend")]
    [InlineData("before <kbd>Ctrl</kbd> after\n\nsecond")]
    [InlineData("<script>bad()</script>\n\n<img src=x onerror=bad()>\n\ntext")]
    [InlineData("# title {onclick=bad()}\n\n[bad](javascript:alert(1))")]
    [InlineData("```mermaid\nflowchart LR\n A --> B\n```\n\nnext")]
    [InlineData("")]
    public void 分块过滤_完整文档语义_与原全文过滤一致(string markdown)
    {
        MarkdownRenderer renderer = new();
        string expected = Reference(markdown);
        Assert.Equal(expected, renderer.RenderBody(markdown));
        Assert.Equal(expected, renderer.RenderBody(markdown));
    }

    [Fact]
    public void 引用与脚注定义变化_已缓存正文_必须更新链接与脚注()
    {
        MarkdownRenderer renderer = new();
        const string first = "[link][ref] and note[^n]\n\n[ref]: https://example.com/old\n\n[^n]: old note";
        string second = first.Replace("/old", "/new", StringComparison.Ordinal).Replace("old note", "new note", StringComparison.Ordinal);
        renderer.RenderDocument(first);
        Assert.Equal(Reference(second), renderer.RenderBody(second));
    }

    [Fact]
    public void 原始Html_跨块或嵌套行内标签_统一回退全文过滤()
    {
        MarkdownRenderer renderer = new();
        foreach (string markdown in new[] { "<div>\n\nfirst\n\nsecond\n\n</div>", "> nested <span>html</span>\n\nnext", "first <a id=\"pragma-line-0\"></a>\n\nsecond" })
        {
            MarkdownRenderResult result = renderer.RenderDocument(markdown);
            Assert.Single(result.Blocks);
            Assert.Equal(Reference(markdown), result.BodyHtml);
        }
    }

    [Fact]
    public void 局部编辑_其余语义块_保持稳定标识且标识唯一()
    {
        MarkdownRenderer renderer = new();
        MarkdownRenderResult first = renderer.RenderDocument("first\n\nsecond\n\nthird");
        MarkdownRenderResult second = renderer.RenderDocument("first\n\nchanged\n\nthird");
        Assert.Equal(first.Blocks[0].Key, second.Blocks[0].Key);
        Assert.NotEqual(first.Blocks[1].Key, second.Blocks[1].Key);
        Assert.Equal(first.Blocks[2].Key, second.Blocks[2].Key);
        MarkdownRenderResult duplicates = MarkdownRenderResult.Create(["<p>same</p>", "<p>same</p>"]);
        Assert.Equal(2, duplicates.Blocks.Select(block => block.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void 图片策略变化_已缓存过滤结果_仍重新校验地址()
    {
        MarkdownRenderer renderer = new();
        const string markdown = "![image](https://example.com/image.png)";
        RemoteImagePolicy trust = new(RemoteImageTrustMode.TrustAll, null);
        Assert.Contains("https://example.com/image.png", renderer.RenderBody(markdown, null, trust), StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com/image.png", renderer.RenderBody(markdown), StringComparison.Ordinal);
        string path = Path.Combine(Path.GetTempPath(), "document.md");
        Assert.Contains("wimd-document.invalid", renderer.RenderBody("![local](one.png)", path), StringComparison.Ordinal);
        Assert.DoesNotContain("wimd-document.invalid", renderer.RenderBody("![local](one.png)", null), StringComparison.Ordinal);
    }

    [Fact]
    public void 密集文档_成组完整语义块_保持全文结果与未变化组标识()
    {
        string markdown = string.Concat(Enumerable.Range(0, 500).Select(index => $"## section {index}\n\nparagraph {index}\n\n"));
        MarkdownRenderer renderer = new();
        MarkdownRenderResult first = renderer.RenderDocument(markdown);
        MarkdownRenderResult second = renderer.RenderDocument(markdown + "\nnew paragraph\n");
        Assert.Equal(Reference(markdown), first.BodyHtml);
        Assert.Equal(Reference(markdown + "\nnew paragraph\n"), second.BodyHtml);
        Assert.InRange(first.Blocks.Count, 2, 100);
        Assert.Equal(first.Blocks[0].Key, second.Blocks[0].Key);
    }

    [Fact]
    public void 作者伪造宿主属性_全文过滤后_不得进入安全正文()
    {
        string html = new MarkdownRenderer().RenderBody("<div data-wimd-block=\"fake\" class=\"wimd-preview-block\">text</div>");
        Assert.DoesNotContain("data-wimd-block", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wimd-preview-block", html, StringComparison.Ordinal);
    }

    private static string Reference(string markdown)
    {
        MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UsePragmaLines().Build();
        string safe = new MarkdownHtmlSanitizer().Sanitize(Markdig.Markdown.ToHtml(markdown, pipeline));
        return new LocalImageUrlResolver().RewriteGeneratedHtml(safe, null);
    }
}
