using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class LocalImageStorageServiceTests
{
    [Theory]
    [InlineData("img", "./img/")]
    [InlineData("./assets\\images/", "./assets/images/")]
    public void 规范化相对目录_有效路径_生成可移植格式(string value, string expected)
    {
        Assert.Equal(expected, LocalImageStorageService.NormalizeRelativeDirectory(value));
    }

    [Theory]
    [InlineData("../img")]
    [InlineData("C:\\images")]
    [InlineData("./CON/")]
    [InlineData("./")]
    public void 规范化相对目录_越界或无效路径_拒绝(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            LocalImageStorageService.NormalizeRelativeDirectory(value));
    }

    [Fact]
    public async Task 保存文件_中文名重复_写入配置目录并增加序号()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = Path.Combine(temporaryDirectory.Path, "说明.md");
        string sourcePath = Path.Combine(temporaryDirectory.Path, "示例 图片.png");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        StoredLocalImage first = await LocalImageStorageService.StoreFileAsync(
            documentPath,
            "./img/",
            sourcePath,
            TestContext.Current.CancellationToken);
        StoredLocalImage second = await LocalImageStorageService.StoreFileAsync(
            documentPath,
            "./img/",
            sourcePath,
            TestContext.Current.CancellationToken);

        Assert.Equal("./img/示例 图片.png", first.MarkdownPath);
        Assert.Equal("./img/示例 图片-2.png", second.MarkdownPath);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(first.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 保存剪贴板Png_目录不存在_创建目录并保持字节()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = Path.Combine(temporaryDirectory.Path, "README.md");
        byte[] pngBytes = [137, 80, 78, 71, 1, 2, 3];

        StoredLocalImage image = await LocalImageStorageService.StorePngAsync(
            documentPath,
            "./assets/images/",
            "image-20260818",
            pngBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal("./assets/images/image-20260818.png", image.MarkdownPath);
        Assert.Equal(pngBytes, await File.ReadAllBytesAsync(image.FilePath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(image.FilePath)!,
            ".wimd-image-*.tmp"));
    }
}
