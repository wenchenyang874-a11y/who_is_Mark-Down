using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewUpdateScriptBuilderTests
{
    [Fact]
    public void Build_RenderedBody_UsesDomReplacementWithoutNavigation()
    {
        string script = PreviewUpdateScriptBuilder.Build("<h1>更新</h1>");

        Assert.Contains("preview.replaceChildren", script, StringComparison.Ordinal);
        Assert.Contains("window.scrollTo", script, StringComparison.Ordinal);
        Assert.DoesNotContain("location", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("navigate", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_HtmlLookingLikeScript_EmbedsItOnlyAsEscapedJsonData()
    {
        string script = PreviewUpdateScriptBuilder.Build("</script><script>alert('x')</script>");

        Assert.DoesNotContain("</script>", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003C", script, StringComparison.Ordinal);
    }
}
