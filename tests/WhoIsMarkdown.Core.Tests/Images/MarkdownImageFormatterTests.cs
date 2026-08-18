using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class MarkdownImageFormatterTests
{
    [Fact]
    public void 创建本地图片语法_中文空格路径_按段编码并转义说明()
    {
        string markdown = MarkdownImageFormatter.CreateLocal(
            "截图]说明",
            "./img/中文 图片.png");

        Assert.Equal("![截图\\]说明](./img/%E4%B8%AD%E6%96%87%20%E5%9B%BE%E7%89%87.png)", markdown);
    }

    [Fact]
    public void 创建远程图片语法_非Https地址_拒绝()
    {
        Assert.Throws<ArgumentException>(() => MarkdownImageFormatter.CreateRemote(
            "示例",
            new Uri("http://example.com/image.png")));
    }
}
