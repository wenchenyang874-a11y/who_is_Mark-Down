using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewContentBuilderTests
{
    [Fact]
    public void Prepare_Navigation_BuildsOnlyPageWithExistingSecurityPolicy()
    {
        PreviewContent content = Content("<p>正文</p>");
        PreparedPreviewContent prepared = new PreviewContentBuilder().Prepare(
            content, null, true, false, TestContext.Current.CancellationToken);
        Assert.NotNull(prepared.NavigationHtml);
        Assert.Contains("script-src &#39;none&#39;", prepared.NavigationHtml, StringComparison.Ordinal);
        Assert.Contains("color: black", prepared.NavigationHtml, StringComparison.Ordinal);
        Assert.Contains("font-size: 16px", prepared.NavigationHtml, StringComparison.Ordinal);
        Assert.Null(prepared.UpdateScript);
        Assert.False(prepared.IsPatch);
        Assert.False(prepared.ContainsMermaid);
    }

    [Fact]
    public void Prepare_OrdinaryPatch_DoesNotBuildPageOrWholeBodyReplacement()
    {
        SpyDocumentBuilder document = new();
        PreviewContentBuilder builder = new(document);
        PreviewContent previous = Content("<p>UNCHANGED_PAYLOAD</p>", "<p>before</p>");
        PreviewContent current = Content("<p>UNCHANGED_PAYLOAD</p>", "<p>after</p>");
        PreparedPreviewContent prepared = builder.Prepare(current, previous.Blocks, false, false,
            TestContext.Current.CancellationToken);
        Assert.Null(prepared.NavigationHtml);
        Assert.True(prepared.IsPatch);
        Assert.Contains("after", prepared.UpdateScript!, StringComparison.Ordinal);
        Assert.DoesNotContain("UNCHANGED_PAYLOAD", prepared.UpdateScript!, StringComparison.Ordinal);
        Assert.Equal(0, document.Pages);
        Assert.Equal(0, document.VisibleBodies);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prepare_ForcedOrUnknownBase_BuildsOnlyReplacement(bool force)
    {
        SpyDocumentBuilder document = new();
        PreviewContent content = Content("<p>whole</p>");
        PreparedPreviewContent prepared = new PreviewContentBuilder(document).Prepare(content,
            force ? content.Blocks : null, false, force, TestContext.Current.CancellationToken);
        Assert.Null(prepared.NavigationHtml);
        Assert.False(prepared.IsPatch);
        Assert.Contains("replaceChildren", prepared.UpdateScript!, StringComparison.Ordinal);
        Assert.Equal(0, document.Pages);
        Assert.Equal(1, document.VisibleBodies);
    }

    [Fact]
    public void Prepare_EmptyDocument_KeepsPlaceholderOutsideBlockPatching()
    {
        PreviewContent content = Content();
        PreviewContentBuilder builder = new();
        PreparedPreviewContent prepared = builder.Prepare(content, [], false, false,
            TestContext.Current.CancellationToken);
        Assert.Null(prepared.Blocks);
        Assert.False(prepared.IsPatch);
        Assert.Contains("preview-empty-state", prepared.UpdateScript!, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_PrebuiltCompatibilityPage_ReusesItWithoutPageConstruction()
    {
        SpyDocumentBuilder document = new();
        PreviewContent content = Content("<pre class=\"mermaid\">A--B</pre>") with { PrebuiltPage = "existing page" };
        PreparedPreviewContent prepared = new PreviewContentBuilder(document).Prepare(content, null, true, false,
            TestContext.Current.CancellationToken);
        Assert.Equal("existing page", prepared.NavigationHtml);
        Assert.True(prepared.ContainsMermaid);
        Assert.Equal(0, document.Pages);
    }

    [Fact]
    public void Prepare_CanceledBeforeOrAfterPageBuild_DoesNotReturnStaleDelivery()
    {
        using CancellationTokenSource before = new();
        before.Cancel();
        SpyDocumentBuilder document = new();
        PreviewContent content = Content("<p>data</p>");
        PreviewContentBuilder builder = new(document);
        Assert.Throws<OperationCanceledException>(() => builder.Prepare(content, null, true, false, before.Token));
        Assert.Equal(0, document.Pages);
        using CancellationTokenSource during = new();
        document.AfterBuild = during.Cancel;
        Assert.Throws<OperationCanceledException>(() => builder.Prepare(content, null, true, false, during.Token));
        Assert.Equal(1, document.Pages);
    }

    [Fact]
    public void Replacement_HtmlLookingLikeHostScript_RemainsEscapedData()
    {
        string script = new PreviewContentBuilder().BuildReplacement(Content("</script><script>bad()</script>"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("</script>", script, StringComparison.Ordinal);
        Assert.Contains("\\u003C", script, StringComparison.Ordinal);
    }

    private static PreviewContent Content(params string[] blocks)
    {
        MarkdownRenderResult rendered = MarkdownRenderResult.Create(blocks, TestContext.Current.CancellationToken);
        return new PreviewContent(rendered.PreviewHtml, rendered.Blocks, "body { color: black; }",
            "body { font-size: 16px; }", RemoteImagePolicy.BlockAll);
    }

    private sealed class SpyDocumentBuilder : IPreviewDocumentBuilder
    {
        public int Pages { get; private set; }
        public int VisibleBodies { get; private set; }
        public Action? AfterBuild { get; set; }

        public string Build(string bodyHtml, string styleSheet, RemoteImagePolicy? remoteImagePolicy = null)
        {
            Pages++;
            AfterBuild?.Invoke();
            return bodyHtml;
        }

        public string GetVisibleBody(string bodyHtml)
        {
            VisibleBodies++;
            return bodyHtml;
        }
    }
}
