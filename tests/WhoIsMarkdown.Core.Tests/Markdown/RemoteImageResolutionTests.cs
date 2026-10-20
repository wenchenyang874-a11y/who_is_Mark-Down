using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class RemoteImageResolutionTests
{
    [Fact]
    public void 重写图片_白名单命中Https地址_保留远程图片()
    {
        LocalImageUrlResolver resolver = new();
        RemoteImagePolicy policy = new(RemoteImageTrustMode.AllowList, ["domain:i.ibb.co"]);

        string result = resolver.RewriteGeneratedHtml(
            "<img src=\"https://i.ibb.co/demo/%E5%9B%BE%E7%89%87.png\" />",
            documentPath: null,
            policy);

        Assert.Contains("https://i.ibb.co/demo/%E5%9B%BE%E7%89%87.png", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 重写图片_黑名单命中地址_替换为空像素()
    {
        LocalImageUrlResolver resolver = new();
        RemoteImagePolicy policy = new(RemoteImageTrustMode.BlockList, ["keyword:tracker"]);

        string result = resolver.RewriteGeneratedHtml(
            "<img src=\"https://cdn.example/tracker.png\" />",
            documentPath: null,
            policy);

        Assert.Contains("data:image/gif;base64,", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.example", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 渲染Markdown_默认允许远程图片()
    {
        MarkdownRenderer renderer = new();

        string result = renderer.RenderBody("![远程](https://i.ibb.co/demo/image.png)");

        Assert.Contains("https://i.ibb.co/demo/image.png", result, StringComparison.Ordinal);
    }
}
