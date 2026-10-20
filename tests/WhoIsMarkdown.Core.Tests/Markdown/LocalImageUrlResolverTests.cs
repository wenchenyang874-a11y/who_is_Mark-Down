using System.Net;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class LocalImageUrlResolverTests : IDisposable
{
    private readonly TemporaryDirectory temporaryDirectory = new();
    private readonly LocalImageUrlResolver resolver = new();

    [Fact]
    public void RewriteGeneratedHtml_RelativeImage_UsesConstrainedVirtualHost()
    {
        string documentPath = Path.Combine(temporaryDirectory.Path, "指南.md");
        string html = "<p><img src=\"pic/%E7%A4%BA%E4%BE%8B.png\" alt=\"示例\" /></p>";

        string result = resolver.RewriteGeneratedHtml(html, documentPath);

        Assert.Contains(
            $"https://{LocalImageUrlResolver.VirtualHostName}/pic/%E7%A4%BA%E4%BE%8B.png",
            result,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.png")]
    public void RewriteGeneratedHtml_UnsafeOrUnsupportedImage_BecomesInert(string source)
    {
        string documentPath = Path.Combine(temporaryDirectory.Path, "指南.md");
        string html = $"<img src=\"{WebUtility.HtmlEncode(source)}\" />";

        string result = resolver.RewriteGeneratedHtml(html, documentPath);

        Assert.Contains("data:image/gif;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain(source, result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com/vector.svg")]
    [InlineData("https://trendshift.io/api/badge/repositories/43622/daily")]
    [InlineData("http://localhost:7351/badge")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    [InlineData("data:image/svg+xml,%3Csvg%2F%3E")]
    public void DefaultImageLoading_DoesNotFilterSvgHttpOrExtensionlessUrls(string source)
    {
        string html = $"<img src=\"{WebUtility.HtmlEncode(source)}\" />";
        Assert.Equal(html, resolver.RewriteGeneratedHtml(html, null));
    }

    [Fact]
    public void ProtocolRelativeImage_UsesHttps()
        => Assert.Contains("src=\"https://example.com/badge\"",
            resolver.RewriteGeneratedHtml("<img src=\"//example.com/badge\" />", null), StringComparison.Ordinal);

    [Fact]
    public void RewriteGeneratedHtml_本地Svg_使用受限虚拟主机()
    {
        string documentPath = Path.Combine(temporaryDirectory.Path, "指南.md");
        const string html = "<img src=\"diagrams/flow.svg\" alt=\"流程图\" />";

        string result = resolver.RewriteGeneratedHtml(html, documentPath);

        Assert.Contains(
            $"https://{LocalImageUrlResolver.VirtualHostName}/diagrams/flow.svg",
            result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteGeneratedHtml_DataImage_RemainsAvailableOffline()
    {
        const string html = "<img src=\"data:image/png;base64,AA==\" />";

        string result = resolver.RewriteGeneratedHtml(html, documentPath: null);

        Assert.Equal(html, result);
    }

    public void Dispose()
    {
        temporaryDirectory.Dispose();
        GC.SuppressFinalize(this);
    }
}
