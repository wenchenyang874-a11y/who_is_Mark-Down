using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class MarkdownRendererTests
{
    private readonly MarkdownRenderer renderer = new();

    [Fact]
    public void RenderBody_WhenCommonExtensionsArePresent_RendersExpectedElements()
    {
        const string markdown = """
            # 标题

            - [x] 完成

            | 名称 | 状态 |
            | --- | --- |
            | 编辑器 | 开发中 |

            ```csharp
            Console.WriteLine("Hello");
            ```
            """;

        string html = renderer.RenderBody(markdown);

        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", html, StringComparison.Ordinal);
        Assert.Contains("<table", html, StringComparison.Ordinal);
        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_WhenRawHtmlIsPresent_DoesNotEmitActiveHtml()
    {
        const string markdown = "<script>alert('x')</script>\n<iframe src=\"https://example.com\"></iframe>";

        string html = renderer.RenderBody(markdown);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderBody_WhenDocumentHasBlocks_AddsSourceLineAnchors()
    {
        string html = renderer.RenderBody("# 标题\n\n正文");

        Assert.Contains("pragma-line-0", html, StringComparison.Ordinal);
        Assert.Contains("pragma-line-2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_Mermaid围栏_保留语言标记和源码行锚点()
    {
        const string markdown = """
            ## 流程

            ```mermaid
            flowchart LR
              A[开始] --> B[完成]
            ```
            """;

        string html = renderer.RenderBody(markdown);

        Assert.Contains("class=\"mermaid\"", html, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", html, StringComparison.Ordinal);
        Assert.Contains("pragma-line-2", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_TaskItems_PreservePerItemSourceLineAnchors()
    {
        string html = renderer.RenderBody("- [ ] 第一项\n- [x] 第二项");

        Assert.Matches("<li[^>]*id=\"pragma-line-0\"[^>]*class=\"task-list-item\"", html);
        Assert.Matches("<li[^>]*id=\"pragma-line-1\"[^>]*class=\"task-list-item\"", html);
    }

    [Fact]
    public void RenderBody_WhenImageIsRelative_ResolvesAgainstDocumentDirectory()
    {
        string documentPath = Path.Combine(Path.GetTempPath(), "文档", "指南.md");

        string html = renderer.RenderBody("![图](pic/example.png)", documentPath);

        Assert.Contains(
            $"https://{LocalImageUrlResolver.VirtualHostName}/pic/example.png",
            html,
            StringComparison.Ordinal);
    }
}
