namespace WhoIsMarkdown.Core.Tests.Presentation;

/// <summary>
/// Regression cover for the unified save-as surface.
///
/// The viewer toolbar used to save one implicit format while WebView2's own context
/// menu saved another, so the same picture produced an SVG from one click and an HTML
/// page from the other. These assertions pin the single shared entry point and the
/// host-owned menus that replaced the browser default.
/// </summary>
public sealed class PreviewImageExportPresentationTests
{
    [Fact]
    public void 预览图片另存为_工具栏按钮提供格式选择而不固定单一格式()
    {
        string viewerXaml = ReadAppSource("PreviewImageWindow.xaml");
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        Assert.Contains("Content=\"另存为 ▾\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAs_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SaveAsButton\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains(
            "public void SetExportOptions(IReadOnlyList<PreviewImageExportOption> options)",
            viewerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public event EventHandler<PreviewImageExportRequestedEventArgs>? ExportRequested;",
            viewerCode,
            StringComparison.Ordinal);
        // The single-format event is gone: leaving it behind would let one surface
        // drift back to its own default format.
        Assert.DoesNotContain("SaveRequested", viewerCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_查看窗口右键菜单由宿主替换且与工具栏同源()
    {
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        Assert.Contains(
            "core.ContextMenuRequested += Core_ContextMenuRequested;",
            viewerCode,
            StringComparison.Ordinal);
        // Both surfaces iterate the same option list, so their entries cannot differ.
        int saveAsClickStart = viewerCode.IndexOf("private void SaveAs_Click(", StringComparison.Ordinal);
        int contextMenuStart = viewerCode.IndexOf(
            "private void Core_ContextMenuRequested(",
            StringComparison.Ordinal);
        Assert.True(saveAsClickStart >= 0 && contextMenuStart > saveAsClickStart);
        Assert.Contains("foreach (PreviewImageExportOption option in exportOptions)", viewerCode[..contextMenuStart], StringComparison.Ordinal);
        Assert.Contains("foreach (PreviewImageExportOption option in exportOptions)", viewerCode[contextMenuStart..], StringComparison.Ordinal);
        // The browser menu is replaced rather than extended, because it could only
        // save the generated host document as HTML.
        Assert.Contains("eventArgs.MenuItems.Clear();", viewerCode, StringComparison.Ordinal);
        Assert.Contains("eventArgs.MenuItems.Add(item);", viewerCode, StringComparison.Ordinal);
        Assert.Contains(
            "core.ContextMenuRequested -= Core_ContextMenuRequested;",
            viewerCode,
            StringComparison.Ordinal);
        // Default menus stay enabled so the same request path still works if a menu
        // ever arrives without host items to show.
        Assert.DoesNotContain(
            "AreDefaultContextMenusEnabled = false",
            viewerCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_主预览的图片右键菜单同样由宿主提供()
    {
        string previewCode = ReadAppSource(Path.Combine("Services", "PreviewWebViewService.cs"));

        Assert.Contains(
            "core.ContextMenuRequested += OnContextMenuRequested;",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoreWebView2ContextMenuTargetKind.Image",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public event EventHandler<PreviewContextImageExportRequestedEventArgs>? PreviewContextImageExportRequested;",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreviewImageExportService.GetDisplayName(descriptor.Extension, descriptor.IsSvg, format)",
            previewCode,
            StringComparison.Ordinal);
        // Which picture the menu belongs to must come from the element under the
        // pointer. Chromium reports no source uri for the generated diagrams rendered
        // from data urls, and rejecting those on HasSourceUri left the browser's
        // single-format menu in place for exactly the pictures WIMD can save in three.
        Assert.DoesNotContain("HasSourceUri", previewCode, StringComparison.Ordinal);
        Assert.Contains("document.elementFromPoint(", previewCode, StringComparison.Ordinal);
        Assert.Contains(
            "const image = element?.closest('main.preview-document img');",
            previewCode,
            StringComparison.Ordinal);
        // Resolving the element needs a round trip into the page, so the entries are
        // built under the event deferral.
        Assert.Contains("eventArgs.GetDeferral()", previewCode, StringComparison.Ordinal);
        Assert.Contains("deferral.Complete();", previewCode, StringComparison.Ordinal);
        // A region without an image keeps the browser menu, which is what still
        // provides text selection actions for the rendered document.
        Assert.Contains(
            "// Nothing to act on: leaving MenuItems untouched keeps the browser menu,",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "core.ContextMenuRequested -= OnContextMenuRequested;",
            previewCode,
            StringComparison.Ordinal);
        // WebView2 only raises CustomItemSelected for items the host still holds.
        Assert.Contains(
            "private readonly List<CoreWebView2ContextMenuItem> contextMenuItems = [];",
            previewCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_两个入口走同一条导出路径()
    {
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");

        Assert.Contains(
            "private async void PreviewImageWindow_ExportRequested(",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private async void PreviewService_PreviewContextImageExportRequested(",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "private async Task ExportPreviewImageAsync(",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "await PreviewImageExport.ExportAsync(",
            previewImageCode,
            StringComparison.Ordinal);
        // The viewer no longer saves through the single-format event.
        Assert.DoesNotContain("SaveRequested", previewImageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewImageWindow_SaveRequested", previewImageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_导出使用新的临时缓存而非查看器缓存()
    {
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");

        int exportStart = previewImageCode.IndexOf(
            "private async void PreviewService_PreviewContextImageExportRequested(",
            StringComparison.Ordinal);
        int exportEnd = previewImageCode.IndexOf(
            "private async Task ExportPreviewImageAsync(",
            exportStart,
            StringComparison.Ordinal);
        Assert.True(exportStart >= 0 && exportEnd > exportStart);
        string exportHandler = previewImageCode[exportStart..exportEnd];

        // A preview export materializes its own copy: the bytes exist only for this
        // save, so the viewer's cache directory is never reused or released.
        Assert.Contains("GetPreviewImageCacheRoot()", exportHandler, StringComparison.Ordinal);
        Assert.Contains("TryDeletePreviewImageCache(cacheDirectory);", exportHandler, StringComparison.Ordinal);
        Assert.Contains(
            "previewImageSaveService.PrepareAsync(",
            exportHandler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_切换图片后刷新可用格式()
    {
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");
        string viewerCode = ReadAppSource("PreviewImageWindow.xaml.cs");

        // Available formats follow the encoding, so the reused window has to receive
        // the new list after it takes over another picture.
        Assert.Contains(
            "existing.SetExportOptions(CreateExportOptions(preparedImage));",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewer.SetExportOptions(CreateExportOptions(preparedImage));",
            previewImageCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool CanLoadAnotherImage => !closed && !initializationFailed;",
            viewerCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_栅格化通过离屏WebView2且不新增渲染依赖()
    {
        string rasterizerCode = ReadAppSource(Path.Combine("Services", "WebView2ImageRasterizer.cs"));
        string packages = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Directory.Packages.props"));

        Assert.Contains(
            ": IPreviewImageRasterizer",
            rasterizerCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "CoreWebView2CapturePreviewImageFormat.Png",
            rasterizerCode,
            StringComparison.Ordinal);
        // WebView2 only composites a surface that is genuinely rendered, so the helper
        // window is shown outside the desktop instead of being collapsed or hidden.
        Assert.Contains("host.Show();", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains("OffscreenCoordinate", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains(
            "image.style.width = '{{width}}px';",
            rasterizerCode,
            StringComparison.Ordinal);
        // The decision was to reuse the browser WIMD already ships rather than add a
        // rasterizer package, so the central package list must stay SVG-free.
        Assert.DoesNotContain("Svg.", packages, StringComparison.Ordinal);
        Assert.DoesNotContain("Skia", packages, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_离屏栅格化回到UI线程执行_不在线程池上创建窗口()
    {
        string rasterizerCode = ReadAppSource(Path.Combine("Services", "WebView2ImageRasterizer.cs"));

        // Core drops the calling synchronization context, so the rasterizer is entered on
        // a thread pool thread. Creating a WPF window there threw "the calling thread must
        // be STA" from an async void handler, which terminated the process and closed
        // every open window with it, losing unsaved documents in all of them.
        Assert.Contains(
            "dispatcher.InvokeAsync(",
            rasterizerCode,
            StringComparison.Ordinal);
        Assert.Contains("Dispatcher.CurrentDispatcher", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains("TryReleaseRasterSurface(view, host);", rasterizerCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_异步脚本结果_通过页面槽位轮询而非依赖Promise()
    {
        string rasterizerCode = ReadAppSource(Path.Combine("Services", "WebView2ImageRasterizer.cs"));
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");

        // ExecuteScriptAsync does not await a promise: an async page script returns the
        // serialization of the pending promise, which read as "the image could not be
        // rendered". Outcomes therefore go into a slot the host polls.
        Assert.Contains("window.wimdRasterMeasurement = null;", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdRasterSized = null;", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains("WebViewScriptResult.RunAsync(", rasterizerCode, StringComparison.Ordinal);
        // No page script may rely on its promise being awaited.
        Assert.DoesNotContain("(async () => {", rasterizerCode, StringComparison.Ordinal);
        // The export entry points are async void handlers, so an escaped exception would
        // terminate the process instead of producing a message.
        Assert.Contains("await RunExportGuardedAsync(", previewImageCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览脚本_宿主等待的结果_一律通过页面槽位轮询而非Promise()
    {
        string previewCode = ReadAppSource(Path.Combine("Services", "PreviewWebViewService.cs"));
        string rasterizerCode = ReadAppSource(Path.Combine("Services", "WebView2ImageRasterizer.cs"));
        string helperCode = ReadAppSource(Path.Combine("Services", "WebViewScriptResult.cs"));

        // ExecuteScriptAsync does not await a promise, so a script written as an async
        // IIFE made every host-side wait a no-op. Three deliberate waits were affected:
        // the Mermaid idle wait, the PDF image settle wait, and the offscreen
        // rasterizer's size measurement.
        Assert.DoesNotContain("(async () =>", previewCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(async () =>", rasterizerCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdMermaidIdle = null;", previewCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdPdfImagesSettled = null;", previewCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdRasterMeasurement = null;", rasterizerCode, StringComparison.Ordinal);
        // One shared implementation, so the rule cannot drift per call site.
        Assert.Contains(
            "internal static class WebViewScriptResult",
            helperCode,
            StringComparison.Ordinal);
        Assert.Contains("WebViewScriptResult.RunAsync(", previewCode, StringComparison.Ordinal);
        Assert.Contains("WebViewScriptResult.RunAsync(", rasterizerCode, StringComparison.Ordinal);
        // Each wait keeps a host-side bound so a page that never reports back cannot
        // stall the preview, the PDF export or the image export.
        Assert.Contains("MermaidIdleTimeout", previewCode, StringComparison.Ordinal);
        Assert.Contains("PdfImageSettleTimeout", previewCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片另存为_测量脚本随文档创建而非导航后注入()
    {
        string rasterizerCode = ReadAppSource(Path.Combine("Services", "WebView2ImageRasterizer.cs"));
        string previewImageCode = ReadAppSource("MainWindow.PreviewImages.cs");

        // Injecting the measurement after NavigateToString raced the navigation: the
        // script could land on the document that was about to be replaced, so the slot was
        // written into a page that no longer existed and the export reported "离屏渲染页面
        // 没有报告图片尺寸" once the preview and the viewer were already running.
        Assert.Contains(
            "AddScriptToExecuteOnDocumentCreatedAsync(MeasureScript)",
            rasterizerCode,
            StringComparison.Ordinal);
        // The host only polls for the slot; it must not inject the script itself.
        Assert.Contains(
            "WebViewScriptResult.PollAsync(\n            core,\n            \"window.wimdRasterMeasurement\",",
            rasterizerCode.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
        Assert.Contains("DescribePageAsync(core)", rasterizerCode, StringComparison.Ordinal);
        // An export failure used to be reported under the title of the open operation,
        // which sent the user looking at the wrong feature.
        Assert.Contains("ExportPreviewImageErrorTitle", previewImageCode, StringComparison.Ordinal);
        Assert.Contains(
            "private const string OpenPreviewImageErrorTitle = \"无法打开预览图片\";",
            previewImageCode,
            StringComparison.Ordinal);
    }

    private static string ReadAppSource(string relativePath) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkdown.App",
            relativePath));

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
