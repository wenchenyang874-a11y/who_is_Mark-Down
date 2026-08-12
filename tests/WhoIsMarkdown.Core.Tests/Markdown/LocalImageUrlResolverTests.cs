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
    [InlineData("https://example.com/tracker.png")]
    [InlineData("../outside.png")]
    [InlineData("script.svg")]
    public void RewriteGeneratedHtml_UnsafeOrUnsupportedImage_BecomesInert(string source)
    {
        string documentPath = Path.Combine(temporaryDirectory.Path, "指南.md");
        string html = $"<img src=\"{WebUtility.HtmlEncode(source)}\" />";

        string result = resolver.RewriteGeneratedHtml(html, documentPath);

        Assert.Contains("data:image/gif;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain(source, result, StringComparison.Ordinal);
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
