using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewUpdateScriptBuilderTests
{
    [Fact]
    public void Build_RenderedBody_UsesDomReplacementWithoutNavigation()
    {
        string script = PreviewUpdateScriptBuilder.Build("<h1>更新</h1>");

        Assert.Contains("preview.replaceChildren", script, StringComparison.Ordinal);
        Assert.Contains("wimd:preview-updated", script, StringComparison.Ordinal);
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

    [Fact]
    public void 分块更新_只传变化块Html_旧块仅携带宿主键()
    {
        MarkdownRenderResult previous = MarkdownRenderResult.Create(["<p>UNCHANGED_PAYLOAD</p>", "<p>old</p>"], TestContext.Current.CancellationToken);
        MarkdownRenderResult current = MarkdownRenderResult.Create(["<p>UNCHANGED_PAYLOAD</p>", "<p>new</p>"], TestContext.Current.CancellationToken);
        string script = PreviewUpdateScriptBuilder.BuildPatch(previous.Blocks, current.Blocks, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("UNCHANGED_PAYLOAD", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", script, StringComparison.Ordinal);
        Assert.Contains(previous.Blocks[0].Key, script, StringComparison.Ordinal);
        Assert.Contains("new", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 分块更新_特殊Html数据_仍只能成为转义Json()
    {
        MarkdownRenderResult current = MarkdownRenderResult.Create(["</script><script>alert('x')</script>"], TestContext.Current.CancellationToken);
        string script = PreviewUpdateScriptBuilder.BuildPatch([], current.Blocks, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("</script>", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003C", script, StringComparison.Ordinal);
    }
}
