using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class PreviewImageSaveServiceTests
{
    [Fact]
    public async Task 保存_文档虚拟主机图片_复制中文路径文件()
    {
        using TemporaryDirectory temporary = new();
        string documentPath = Path.Combine(temporary.Path, "说明.md");
        string imageDirectory = Path.Combine(temporary.Path, "img");
        Directory.CreateDirectory(imageDirectory);
        await File.WriteAllTextAsync(documentPath, "# test", TestContext.Current.CancellationToken);
        byte[] expected = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(
            Path.Combine(imageDirectory, "截图.png"),
            expected,
            TestContext.Current.CancellationToken);
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("本地图片不应联网")));
        PreviewImageSaveSource source = service.Resolve(
            "https://wimd-document.invalid/img/%E6%88%AA%E5%9B%BE.png",
            documentPath,
            "截图",
            RemoteImagePolicy.BlockAll);
        string target = Path.Combine(temporary.Path, "副本.png");

        bool saved = await service.SaveAsync(
            source,
            target,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(expected, await File.ReadAllBytesAsync(
            target,
            TestContext.Current.CancellationToken));
        Assert.Equal("截图.png", source.SuggestedFileName);
    }

    [Fact]
    public async Task 准备后另存_原图被修改_保存查看器已验证的字节()
    {
        using TemporaryDirectory temporary = new();
        string documentPath = Path.Combine(temporary.Path, "README.md");
        string sourcePath = Path.Combine(temporary.Path, "source.png");
        byte[] expected = [137, 80, 78, 71, 1, 2, 3];
        await File.WriteAllTextAsync(documentPath, "# test", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(sourcePath, expected, TestContext.Current.CancellationToken);
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("本地图片不应联网")));
        PreviewImageSaveSource source = service.Resolve(
            "https://wimd-document.invalid/source.png",
            documentPath,
            null,
            RemoteImagePolicy.BlockAll);
        string cacheDirectory = Path.Combine(temporary.Path, "viewer-cache");
        PreparedPreviewImage prepared = await service.PrepareAsync(
            source,
            cacheDirectory,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(sourcePath, [9, 9, 9], TestContext.Current.CancellationToken);
        string target = Path.Combine(temporary.Path, "saved.png");

        bool saved = await service.SavePreparedAsync(
            prepared,
            target,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(expected, await File.ReadAllBytesAsync(
            target,
            TestContext.Current.CancellationToken));
        Assert.Equal("source.png", prepared.SuggestedFileName);
    }

    [Fact]
    public void 解析_虚拟主机地址包含编码穿越_拒绝越出文档目录()
    {
        using TemporaryDirectory temporary = new();
        string documentPath = Path.Combine(temporary.Path, "readme.md");
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("不应联网")));

        PreviewImageSaveException exception = Assert.Throws<PreviewImageSaveException>(() =>
            service.Resolve(
                "https://wimd-document.invalid/%2E%2E%2Fsecret.png",
                documentPath,
                null,
                RemoteImagePolicy.BlockAll));

        Assert.Contains("不安全", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 保存_内嵌Png图片_解码后原子写入目标()
    {
        using TemporaryDirectory temporary = new();
        byte[] expected = [137, 80, 78, 71, 13, 10, 26, 10];
        string dataUri = $"data:image/png;base64,{Convert.ToBase64String(expected)}";
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("内嵌图片不应联网")));
        PreviewImageSaveSource source = service.Resolve(
            dataUri,
            null,
            "演示截图",
            RemoteImagePolicy.BlockAll);
        string target = Path.Combine(temporary.Path, "演示截图.png");

        await service.SaveAsync(source, target, TestContext.Current.CancellationToken);

        Assert.Equal(expected, await File.ReadAllBytesAsync(
            target,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mermaid图表_安全Svg_可在查看器缓存并另存矢量文件()
    {
        using TemporaryDirectory temporary = new();
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 40">
              <style>#node { fill: #eef0fa; } #arrow { marker-end: url(#tip); }</style>
              <defs><marker id="tip"><path d="M0 0L4 2L0 4Z" /></marker></defs>
              <rect id="node" width="80" height="30" />
              <path id="arrow" d="M80 15L110 15" />
            </svg>
            """;
        string dataUri = $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("生成的 SVG 不应联网")));

        PreviewImageSaveSource source = service.ResolveGeneratedSvgDataUri(dataUri, "训练流程");
        PreparedPreviewImage prepared = await service.PrepareAsync(
            source,
            Path.Combine(temporary.Path, "cache"),
            TestContext.Current.CancellationToken);
        string target = Path.Combine(temporary.Path, "训练流程.svg");
        bool saved = await service.SavePreparedAsync(
            prepared,
            target,
            TestContext.Current.CancellationToken);

        Assert.True(saved);
        Assert.Equal(".svg", prepared.Extension);
        Assert.Equal("训练流程.svg", prepared.SuggestedFileName);
        Assert.Equal(svg, await File.ReadAllTextAsync(
            target,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Mermaid图表_含脚本的Svg_拒绝进入查看器()
    {
        const string unsafeSvg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>";
        string dataUri = $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(unsafeSvg))}";
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("生成的 SVG 不应联网")));

        PreviewImageSaveException exception = Assert.Throws<PreviewImageSaveException>(() =>
            service.ResolveGeneratedSvgDataUri(dataUri, null));

        Assert.Contains("不安全元素", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 普通图片解析_用户提供Svg数据_仍然拒绝()
    {
        const string dataUri = "data:image/svg+xml;base64,PHN2Zy8+";
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("不应联网")));

        Assert.Throws<PreviewImageSaveException>(() => service.Resolve(
            dataUri,
            null,
            null,
            RemoteImagePolicy.BlockAll));
    }

    [Fact]
    public async Task 保存_受信任Https图片_校验类型并下载()
    {
        byte[] expected = [137, 80, 78, 71];
        RecordingHandler handler = new(request =>
        {
            Assert.Equal("trusted.example", request.RequestUri?.Host);
            ByteArrayContent content = new(expected);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using PreviewImageSaveService service = new(handler);
        RemoteImagePolicy policy = new(RemoteImageTrustMode.AllowList, ["domain:trusted.example"]);
        PreviewImageSaveSource source = service.Resolve(
            "https://trusted.example/picture.png",
            null,
            null,
            policy);
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "picture.png");

        await service.SaveAsync(source, target, TestContext.Current.CancellationToken);

        Assert.Equal(expected, await File.ReadAllBytesAsync(
            target,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
        Assert.True(source.RequiresNetwork);
    }

    [Fact]
    public async Task 保存_远程图片重定向到未信任域名_下载前拒绝目标()
    {
        RecordingHandler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://blocked.example/picture.png");
            return response;
        });
        using PreviewImageSaveService service = new(handler);
        RemoteImagePolicy policy = new(RemoteImageTrustMode.AllowList, ["domain:trusted.example"]);
        PreviewImageSaveSource source = service.Resolve(
            "https://trusted.example/picture.png",
            null,
            null,
            policy);
        using TemporaryDirectory temporary = new();

        PreviewImageSaveException exception = await Assert.ThrowsAsync<PreviewImageSaveException>(() =>
            service.SaveAsync(
                source,
                Path.Combine(temporary.Path, "picture.png"),
                TestContext.Current.CancellationToken));

        Assert.Contains("未受信任", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void 解析_远程图片不在信任策略内_不提供保存来源()
    {
        using PreviewImageSaveService service = new(new RecordingHandler(
            _ => throw new InvalidOperationException("不应联网")));

        PreviewImageSaveException exception = Assert.Throws<PreviewImageSaveException>(() =>
            service.Resolve(
                "https://blocked.example/picture.png",
                null,
                null,
                RemoteImagePolicy.BlockAll));

        Assert.Contains("不允许", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}
