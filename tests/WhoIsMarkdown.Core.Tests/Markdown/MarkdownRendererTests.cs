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
        Assert.Contains("<table>", html, StringComparison.Ordinal);
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
}
