using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class RemoteImagePolicyTests
{
    [Fact]
    public void 允许列表_规则类型命中完整地址_允许图片()
    {
        RemoteImagePolicy policy = new(
            RemoteImageTrustMode.AllowList,
            [
                "domain:i.ibb.co",
                "prefix:https://cdn.example.com/public/",
                "suffix:/cover.png",
                "keyword:avatar",
                "regex:^https://images\\.example\\.com/.+\\.jpg$",
            ]);

        Assert.True(policy.Allows(new Uri("https://i.ibb.co/id/demo.png")));
        Assert.True(policy.Allows(new Uri("https://cdn.example.com/public/a.webp")));
        Assert.True(policy.Allows(new Uri("https://other.example/a/cover.png")));
        Assert.True(policy.Allows(new Uri("https://other.example/avatar/1.gif")));
        Assert.True(policy.Allows(new Uri("https://images.example.com/a/photo.jpg")));
        Assert.False(policy.Allows(new Uri("https://other.example/private.png")));
    }

    [Fact]
    public void 黑名单_命中任一规则_阻止图片()
    {
        RemoteImagePolicy policy = new(
            RemoteImageTrustMode.BlockList,
            ["domain:tracker.example", "keyword:tracking-pixel"]);

        Assert.False(policy.Allows(new Uri("https://tracker.example/a.png")));
        Assert.False(policy.Allows(new Uri("https://cdn.example/tracking-pixel.gif")));
        Assert.True(policy.Allows(new Uri("https://cdn.example/photo.png")));
    }

    [Theory]
    [InlineData(RemoteImageTrustMode.BlockAll, false)]
    [InlineData(RemoteImageTrustMode.TrustAll, true)]
    public void 全局信任模式_没有规则_按模式处理(RemoteImageTrustMode mode, bool expected)
    {
        RemoteImagePolicy policy = new(mode, []);

        Assert.Equal(expected, policy.Allows(new Uri("https://example.com/image.png")));
        Assert.False(policy.Allows(new Uri("http://example.com/image.png")));
    }

    [Fact]
    public void 内容安全策略来源_仅域名白名单_保持精确域名()
    {
        RemoteImagePolicy exactPolicy = new(
            RemoteImageTrustMode.AllowList,
            ["domain:cdn.example.com", "domain:i.ibb.co"]);
        RemoteImagePolicy flexiblePolicy = new(
            RemoteImageTrustMode.AllowList,
            ["prefix:https://cdn.example.com/"]);

        Assert.Equal(
            ["https://cdn.example.com", "https://i.ibb.co"],
            exactPolicy.GetContentSecurityPolicySources());
        Assert.Equal(["https:"], flexiblePolicy.GetContentSecurityPolicySources());
    }

    [Theory]
    [InlineData("unknown:value")]
    [InlineData("regex:(unclosed")]
    [InlineData("domain:https://example.com")]
    [InlineData("domain:*.example.com")]
    public void 规范化规则_规则格式无效_拒绝保存(string rule)
    {
        Assert.Throws<ArgumentException>(() => RemoteImagePolicy.NormalizeRules([rule]));
    }
}
