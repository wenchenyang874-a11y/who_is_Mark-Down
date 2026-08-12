using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class MarkdownHtmlSanitizerTests
{
    private readonly MarkdownRenderer renderer = new();

    [Fact]
    public void RenderBody_AllowlistedRawHtml_PreservesUsefulLayoutElements()
    {
        const string markdown = """
            <div align="center">
              <img src="data:image/png;base64,AA==" width="104" alt="WIMD">
              <h1>WIMD</h1>
              <details open><summary>快捷键</summary><kbd>Ctrl</kbd>+<kbd>B</kbd></details>
              <table><tr><td width="50%">左侧</td><td>右侧</td></tr></table>
            </div>
            """;

        string html = renderer.RenderBody(markdown);

        Assert.Contains("<div align=\"center\">", html, StringComparison.Ordinal);
        Assert.Contains("width=\"104\"", html, StringComparison.Ordinal);
        Assert.Contains("<details open", html, StringComparison.Ordinal);
        Assert.Contains("<summary>快捷键</summary>", html, StringComparison.Ordinal);
        Assert.Contains("<kbd>Ctrl</kbd>", html, StringComparison.Ordinal);
        Assert.Contains("width=\"50%\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_DangerousRawHtml_RemovesExecutableContentAndAttributes()
    {
        const string markdown = """
            <script>alert('x')</script>
            <style>body { display: none; }</style>
            <iframe src="https://example.com"></iframe>
            <form action="https://example.com"><input type="text"></form>
            <div style="position:fixed" onclick="alert(1)">正文</div>
            <a href="javascript:alert(1)" target="_blank">链接</a>
            <img src="https://example.com/tracker.png" onerror="alert(1)">
            """;

        string html = renderer.RenderBody(markdown, Path.Combine(Path.GetTempPath(), "document.md"));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com/tracker.png", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderBody_UnsafePresentationValues_RemovesOutOfRangeValuesAndClasses()
    {
        const string markdown = """
            <div align="middle" class="preview-empty-state attacker">正文</div>
            <img src="data:image/png;base64,AA==" width="999999" height="25%">
            <pre><code class="language-csharp attacker">code</code></pre>
            """;

        string html = renderer.RenderBody(markdown);

        Assert.DoesNotContain("align=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("width=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("height=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview-empty-state", html, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", html, StringComparison.Ordinal);
        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderBody_DataLink_RemovesHrefButKeepsText()
    {
        const string markdown = "<a href=\"data:text/html,unsafe\">不要打开</a>";

        string html = renderer.RenderBody(markdown);

        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("不要打开", html, StringComparison.Ordinal);
    }
}
