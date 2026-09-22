using System.Text;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Images;

/// <summary>
/// Covers the save-as formats that unify the viewer toolbar with the right-click
/// menus. Every export revalidates the prepared cache file and lands on disk through
/// the shared atomic replacement, so the tests drive the public entry point rather
/// than the internal helpers.
/// </summary>
public sealed class PreviewImageExportServiceTests
{
    [Fact]
    public void 可用格式_矢量图_依次提供原格式PNG与HTML()
    {
        IReadOnlyList<PreviewImageExportFormat> formats =
            PreviewImageExportService.GetAvailableFormats(".svg");

        Assert.Equal(
            [
                PreviewImageExportFormat.Original,
                PreviewImageExportFormat.Png,
                PreviewImageExportFormat.Html,
            ],
            formats);
        Assert.Equal(
            "SVG 矢量图（.svg）",
            PreviewImageExportService.GetDisplayName(".svg", isSvg: true, PreviewImageExportFormat.Original));
    }

    [Fact]
    public async Task 可用格式_PNG位图_不再重复提供PNG()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareBitmapAsync(temporary, "picture.png");

        IReadOnlyList<PreviewImageExportFormat> formats =
            PreviewImageExportService.GetAvailableFormats(prepared);

        Assert.Equal(
            [PreviewImageExportFormat.Original, PreviewImageExportFormat.Html],
            formats);
        Assert.Equal(
            "原格式（.png）",
            PreviewImageExportService.GetDisplayName(prepared, PreviewImageExportFormat.Original));
    }

    [Fact]
    public async Task 建议文件名_按所选格式替换扩展名()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareGeneratedDiagramAsync(
            temporary,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100%\" viewBox=\"0 0 400 200\"><rect width=\"400\" height=\"200\"/></svg>");

        Assert.Equal(
            "Mermaid 图表.svg",
            PreviewImageExportService.GetSuggestedFileName(prepared, PreviewImageExportFormat.Original));
        Assert.Equal(
            "Mermaid 图表.png",
            PreviewImageExportService.GetSuggestedFileName(prepared, PreviewImageExportFormat.Png));
        Assert.Equal(
            "Mermaid 图表.html",
            PreviewImageExportService.GetSuggestedFileName(prepared, PreviewImageExportFormat.Html));
    }

    [Fact]
    public async Task 导出PNG_矢量图_按viewBox补全像素尺寸后交给栅格化器()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareGeneratedDiagramAsync(
            temporary,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100%\" height=\"100%\" viewBox=\"0 0 1200 600\"><rect width=\"1200\" height=\"600\"/></svg>");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71, 13, 10, 26, 10]);
        string target = Path.Combine(temporary.Path, "diagram.png");

        bool saved = await new PreviewImageExportService(new PreviewImageSaveService())
            .ExportAsync(
                prepared,
                PreviewImageExportFormat.Png,
                target,
                rasterizer,
                TestContext.Current.CancellationToken);

        Assert.True(saved);
        // A percentage size has no pixel meaning, so the viewBox has to supply it.
        Assert.Equal(1200, rasterizer.ReceivedWidth);
        Assert.Equal(600, rasterizer.ReceivedHeight);
        Assert.Equal("image.svg", rasterizer.ReceivedFileName);
        string normalized = Encoding.UTF8.GetString(rasterizer.ReceivedBytes!);
        Assert.Contains("width=\"1200\"", normalized, StringComparison.Ordinal);
        Assert.Contains("height=\"600\"", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("100%", normalized, StringComparison.Ordinal);
        Assert.Equal(
            rasterizer.PngBytes,
            await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 导出PNG_矢量图超过上限_按长边等比缩小()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareGeneratedDiagramAsync(
            temporary,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 12288 6144\"><rect width=\"12288\" height=\"6144\"/></svg>");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        string target = Path.Combine(temporary.Path, "huge.png");

        await new PreviewImageExportService(new PreviewImageSaveService())
            .ExportAsync(
                prepared,
                PreviewImageExportFormat.Png,
                target,
                rasterizer,
                TestContext.Current.CancellationToken);

        // An unbounded diagram would ask the offscreen surface for a window larger
        // than the desktop can host, so the long side is capped and the aspect kept.
        Assert.Equal(PreviewImageExportService.MaximumRasterDimension, rasterizer.ReceivedWidth);
        Assert.Equal(PreviewImageExportService.MaximumRasterDimension / 2, rasterizer.ReceivedHeight);
    }

    [Fact]
    public async Task 导出PNG_位图源_交给栅格化器使用图片自身尺寸()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareBitmapAsync(temporary, "photo.jpg");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        string target = Path.Combine(temporary.Path, "photo.png");

        await new PreviewImageExportService(new PreviewImageSaveService())
            .ExportAsync(
                prepared,
                PreviewImageExportFormat.Png,
                target,
                rasterizer,
                TestContext.Current.CancellationToken);

        Assert.Null(rasterizer.ReceivedWidth);
        Assert.Null(rasterizer.ReceivedHeight);
        Assert.Equal("image.jpg", rasterizer.ReceivedFileName);
    }

    [Fact]
    public async Task 导出HTML_自包含页面_不引用任何外部地址()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareGeneratedDiagramAsync(
            temporary,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 200\"><rect width=\"400\" height=\"200\"/></svg>");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        string target = Path.Combine(temporary.Path, "diagram.html");

        bool saved = await new PreviewImageExportService(new PreviewImageSaveService())
            .ExportAsync(
                prepared,
                PreviewImageExportFormat.Html,
                target,
                rasterizer,
                TestContext.Current.CancellationToken);

        Assert.True(saved);
        string page = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains("<meta charset=\"utf-8\">", page, StringComparison.Ordinal);
        Assert.Contains("data:image/svg+xml;base64,", page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", page, StringComparison.Ordinal);
        // The page must survive on its own: no companion file and no network fetch.
        Assert.DoesNotContain("http://", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", page, StringComparison.OrdinalIgnoreCase);
        Assert.Null(rasterizer.ReceivedBytes);
    }

    [Fact]
    public async Task 导出原格式_矢量图_写回安全过滤后的SVG()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareGeneratedDiagramAsync(
            temporary,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 200\"><rect width=\"400\" height=\"200\"/></svg>");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        string target = Path.Combine(temporary.Path, "diagram.svg");

        bool saved = await new PreviewImageExportService(new PreviewImageSaveService())
            .ExportAsync(
                prepared,
                PreviewImageExportFormat.Original,
                target,
                rasterizer,
                TestContext.Current.CancellationToken);

        Assert.True(saved);
        string savedSvg = await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken);
        Assert.Contains("<svg", savedSvg, StringComparison.Ordinal);
        Assert.Null(rasterizer.ReceivedBytes);
    }

    [Fact]
    public async Task 导出_目标扩展名与所选格式不符_拒绝写入()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareBitmapAsync(temporary, "picture.png");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        string target = Path.Combine(temporary.Path, "picture.html");

        PreviewImageSaveException exception = await Assert.ThrowsAsync<PreviewImageSaveException>(
            () => new PreviewImageExportService(new PreviewImageSaveService())
                .ExportAsync(
                    prepared,
                    PreviewImageExportFormat.Png,
                    target,
                    rasterizer,
                    TestContext.Current.CancellationToken));

        Assert.Contains(".png", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task 导出_查看器临时图片已失效_拒绝写入()
    {
        using TemporaryDirectory temporary = new();
        PreparedPreviewImage prepared = await PrepareBitmapAsync(temporary, "picture.png");
        RecordingRasterizer rasterizer = new([137, 80, 78, 71]);
        File.Delete(prepared.FilePath);
        string target = Path.Combine(temporary.Path, "picture.html");

        await Assert.ThrowsAsync<PreviewImageSaveException>(
            () => new PreviewImageExportService(new PreviewImageSaveService())
                .ExportAsync(
                    prepared,
                    PreviewImageExportFormat.Html,
                    target,
                    rasterizer,
                    TestContext.Current.CancellationToken));

        Assert.False(File.Exists(target));
    }

    private static async Task<PreparedPreviewImage> PrepareBitmapAsync(
        TemporaryDirectory temporary,
        string fileName)
    {
        string documentPath = Path.Combine(temporary.Path, "README.md");
        await File.WriteAllTextAsync(documentPath, "# test", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(temporary.Path, fileName),
            [137, 80, 78, 71, 1, 2, 3],
            TestContext.Current.CancellationToken);
        using PreviewImageSaveService service = new();
        PreviewImageSaveSource source = service.Resolve(
            $"https://wimd-document.invalid/{fileName}",
            documentPath,
            null,
            RemoteImagePolicy.BlockAll);
        return await service.PrepareAsync(
            source,
            Path.Combine(temporary.Path, "viewer-cache"),
            TestContext.Current.CancellationToken);
    }

    private static async Task<PreparedPreviewImage> PrepareGeneratedDiagramAsync(
        TemporaryDirectory temporary,
        string svg)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        using PreviewImageSaveService service = new();
        PreviewImageSaveSource source = service.ResolveGeneratedSvgDataUri(
            $"data:image/svg+xml;base64,{payload}",
            "Mermaid 图表");
        return await service.PrepareAsync(
            source,
            Path.Combine(temporary.Path, "viewer-cache"),
            TestContext.Current.CancellationToken);
    }

    private sealed class RecordingRasterizer(byte[] pngBytes) : IPreviewImageRasterizer
    {
        public byte[] PngBytes { get; } = pngBytes;

        public byte[]? ReceivedBytes { get; private set; }

        public string? ReceivedFileName { get; private set; }

        public int? ReceivedHeight { get; private set; }

        public int? ReceivedWidth { get; private set; }

        public Task<byte[]> RasterizeToPngAsync(
            byte[] imageBytes,
            string fileName,
            int? pixelWidth,
            int? pixelHeight,
            CancellationToken cancellationToken = default)
        {
            ReceivedBytes = imageBytes;
            ReceivedFileName = fileName;
            ReceivedWidth = pixelWidth;
            ReceivedHeight = pixelHeight;
            return Task.FromResult(PngBytes);
        }
    }
}
