namespace WhoIsMarkdown.Core.Tests.Presentation;

/// <summary>
/// Regression cover for the modeless preview image viewer. The desktop project is
/// not referenced from the core test assembly, so these assertions read the WPF
/// sources as text and pin the host-script and window-lifetime decisions that a
/// compile-only check cannot catch.
/// </summary>
public sealed class PreviewImageViewerPresentationTests
{
    [Fact]
    public void 预览图片查看窗口_图片在DOMContentLoaded之前完成_仍按适应窗口居中显示()
    {
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        // A viewer-cache file normally finishes decoding before DOMContentLoaded.
        // Subscribing to 'load' alone would leave the image at 100% pinned to the
        // top-left corner because that event had already fired by then.
        Assert.Contains(
            "if (image.complete && image.naturalWidth > 0) {",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "image.addEventListener('load', fitAfterLayout, { once: true });",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "const fitAfterLayout = () => requestAnimationFrame(() => fit());",
            viewerCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "image.addEventListener('load', fit, { once: true });",
            viewerCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片查看窗口_首帧布局未完成_按根元素尺寸计算适应倍率()
    {
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        // Fitting against a zero-sized viewport would clamp the scale to the 5%
        // minimum, so the fallback to the root box must stay in the script.
        Assert.Contains(
            "const viewportWidth = () => viewport.clientWidth || document.documentElement.clientWidth || 0;",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "const viewportHeight = () => viewport.clientHeight || document.documentElement.clientHeight || 0;",
            viewerCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片查看窗口_矢量图物化时补齐像素尺寸_不依赖浏览器默认尺寸()
    {
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");
        string saveService = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkDown.Core",
            "Images",
            "PreviewImageSaveService.cs"));
        string normalizer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkDown.Core",
            "Images",
            "SvgPixelSizeNormalizer.cs"));

        // Mermaid emits width="100%" with a viewBox, and Chromium then reports its
        // 300x150 default through the image element. The viewer fitted against that
        // number and showed a 1260-wide diagram at 345% pinned to the left edge, so the
        // real size is resolved while the vector is still readable as XML.
        Assert.Contains(
            "SvgPixelSizeNormalizer.Apply(svgBytes)",
            saveService,
            StringComparison.Ordinal);
        Assert.Contains("viewBox", normalizer, StringComparison.Ordinal);
        Assert.Contains("width=\"", normalizer, StringComparison.Ordinal);
        // The materialized copy must stay as close to the sanitized input as possible,
        // so only the two size attributes are added.
        Assert.Contains("OmitXmlDeclaration = true", normalizer, StringComparison.Ordinal);
        // The surface can be laid out after the document loads and that raises no window
        // resize, so the viewport itself is observed and the fit is redone.
        Assert.Contains(
            "new ResizeObserver(() => { if (fitted) fit(); }).observe(viewport);",
            viewerCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片查看窗口_再次打开图片_复用已有窗口而不关闭重建()
    {
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        int openHandlerStart = previewImageCode.IndexOf(
            "private async void PreviewService_PreviewImageOpenRequested",
            StringComparison.Ordinal);
        int obtainHandlerStart = previewImageCode.IndexOf(
            "private void ObtainPreviewImageWindow(",
            StringComparison.Ordinal);
        Assert.True(openHandlerStart >= 0 && obtainHandlerStart > openHandlerStart);
        string openHandler = previewImageCode[openHandlerStart..obtainHandlerStart];
        Assert.Contains(
            "ObtainPreviewImageWindow(preparedImage, eventArgs.AlternativeText);",
            openHandler,
            StringComparison.Ordinal);
        // The open path must never create or show a window itself; that would
        // reintroduce the close-and-reopen behavior this fix removed.
        Assert.DoesNotContain("viewer.Show();", openHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("ClosePreviewImageWindow();", openHandler, StringComparison.Ordinal);

        Assert.Contains(
            "existing.CanLoadAnotherImage",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains("existing.LoadImage(", previewImageCode, StringComparison.Ordinal);
        Assert.Contains(
            "public bool CanLoadAnotherImage => !closed && !initializationFailed;",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public PreparedPreviewImage? LoadImage(",
            viewerCode,
            StringComparison.Ordinal);
        // A fresh window is still required whenever the viewer cannot take over the
        // image, so Show must remain on the fallback path.
        Assert.Contains("viewer.Show();", previewImageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("viewer.ShowDialog()", previewImageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片查看窗口_切换图片_释放被替换图片的缓存目录()
    {
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");

        // Reusing the window means the previous cache directory is no longer
        // reclaimed by the window's Closed handler, so the swap must release it.
        Assert.Contains(
            "private void ReleaseReplacedPreviewImageCache(PreparedPreviewImage replaced)",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseReplacedPreviewImageCache(viewer.PreparedImage);",
            previewImageCode,
            StringComparison.Ordinal);
        // An image that is still being copied to the user's destination must keep
        // its bytes until SavePreparedAsync completes.
        Assert.Contains(
            "if (!ReferenceEquals(previewImageBeingSaved, replaced))",
            previewImageCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片查看窗口_每次载入_使用一次性虚拟主机名避免旧图缓存()
    {
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        // Chromium reuses the cached response for an identical origin and file name.
        // Re-displaying an image that changed on disk, or a second image that shares
        // a file name, therefore needs a host name that has never been used before.
        Assert.Contains(
            "private const string ViewerHostNamePrefix = \"wimd-image-viewer-\";",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Concat(ViewerHostNamePrefix, Guid.NewGuid().ToString(\"N\"))",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "core.ClearVirtualHostNameToFolderMapping(viewerHostName);",
            viewerCode,
            StringComparison.Ordinal);
    }

    private static string ReadAppSource(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkdown.App",
            fileName));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WhoIsMarkdown.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
