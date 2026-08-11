using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewDocumentBuilderTests
{
    [Fact]
    public void Build_AlwaysAddsRestrictiveContentSecurityPolicy()
    {
        PreviewDocumentBuilder builder = new();

        string page = builder.Build("<h1>Title</h1>", "body { color: black; }");

        Assert.Contains("default-src &#39;none&#39;", page, StringComparison.Ordinal);
        Assert.Contains("script-src &#39;none&#39;", page, StringComparison.Ordinal);
        Assert.Contains("img-src data: file:", page, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenBodyIsEmpty_AddsVisibleGettingStartedState()
    {
        PreviewDocumentBuilder builder = new();

        string page = builder.Build(string.Empty, string.Empty);

        Assert.Contains("preview-empty-state", page, StringComparison.Ordinal);
        Assert.Contains("开始写点什么吧", page, StringComparison.Ordinal);
    }
}
