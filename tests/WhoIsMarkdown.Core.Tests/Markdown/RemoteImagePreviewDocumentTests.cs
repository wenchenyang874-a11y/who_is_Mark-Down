using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class RemoteImagePreviewDocumentTests
{
    [Fact]
    public void 构建预览_精确域名白名单_Csp只加入对应域名()
    {
        PreviewDocumentBuilder builder = new();
        RemoteImagePolicy policy = new(RemoteImageTrustMode.AllowList, ["domain:i.ibb.co"]);

        string page = builder.Build("<p>内容</p>", string.Empty, policy);

        Assert.Contains("https://i.ibb.co", page, StringComparison.Ordinal);
        Assert.DoesNotContain("img-src https:", page, StringComparison.Ordinal);
        Assert.Contains("script-src &#39;none&#39;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void 构建预览_关键词白名单_Csp开放Https图片但保持其他资源关闭()
    {
        PreviewDocumentBuilder builder = new();
        RemoteImagePolicy policy = new(RemoteImageTrustMode.AllowList, ["keyword:public-image"]);

        string page = builder.Build("<p>内容</p>", string.Empty, policy);

        Assert.Contains("img-src data: https://wimd-document.invalid https:", page, StringComparison.Ordinal);
        Assert.Contains("default-src &#39;none&#39;", page, StringComparison.Ordinal);
        Assert.Contains("script-src &#39;none&#39;", page, StringComparison.Ordinal);
    }
}
