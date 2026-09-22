using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Renders image bytes to PNG through an offscreen WebView2 surface.
///
/// WIMD ships no SVG rasterizer and deliberately adds no image-rendering dependency,
/// so the browser engine the application already hosts is reused instead. The hosting
/// window is placed outside the visible desktop but is still shown: WebView2 only
/// composites — and therefore only captures — a surface that is genuinely rendered,
/// so a collapsed or never-shown control would produce nothing.
///
/// The offscreen page never receives page-authored script or remote content. It loads
/// exactly one validated image from a one-shot virtual host, and the size it reports
/// back is the only thing the host reads from it.
///
/// Two failure modes found by running this against the real window are recorded here
/// because neither is visible from the code alone:
///  1. The export rules live in the core layer and use ConfigureAwait(false), so this
///     implementation is entered on a thread pool thread. WPF windows and WebView2
///     controllers are STA-only, so every step is marshalled to the dispatcher. Without
///     that, `new Window()` threw "the calling thread must be STA" inside an async void
///     handler and terminated the whole process, closing every WIMD window with it.
///  2. ExecuteScriptAsync does not await a promise. An async page script therefore
///     returned the serialization of the pending promise ({}) instead of the result,
///     which read as "the image could not be rendered". Page outcomes are published
///     into a global slot that the host polls for instead.
/// </summary>
internal sealed class WebView2ImageRasterizer : IPreviewImageRasterizer
{
    private const string RasterHostName = "wimd-image-raster.invalid";

    /// <summary>
    /// Offscreen placement. A window placed entirely outside the desktop is still
    /// composited by the browser process, which is what makes the capture work without
    /// showing anything to the user.
    /// </summary>
    private const int OffscreenCoordinate = -32000;

    /// <summary>
    /// Longest side of the capture. Matches the core export limit so the requested
    /// size always fits the window Windows can actually host.
    /// </summary>
    private const int MaximumCaptureDimension = PreviewImageExportService.MaximumRasterDimension;

    private const int MaximumMeasurementLength = 64 * 1024;

    private static readonly TimeSpan RasterStepTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Waits for the image to be reachable and decoded, then publishes its intrinsic
    /// size and the device pixel ratio into the result slot. Written as a polling script
    /// rather than a promise because ExecuteScriptAsync does not await one.
    ///
    /// This is registered as a document-created script, not injected after navigating.
    /// Injecting it meant the script could land on the document that was still being
    /// replaced, so the slot was written into a page that was about to be discarded and
    /// the host polled a slot that no longer existed — which is exactly what happened
    /// once two other WebView2 controls were already running and navigation slowed down.
    /// </summary>
    private const string MeasureScript = """
        (() => {
          const deadline = Date.now() + 12000;
          const publish = () => {
            const image = document.getElementById('image');
            if (!image) {
              if (Date.now() < deadline) { setTimeout(publish, 30); return; }
              window.wimdRasterMeasurement = { ok: false, complete: false, width: 0, height: 0 };
              return;
            }
            if (!image.complete) {
              if (Date.now() < deadline) { setTimeout(publish, 30); return; }
            }
            window.wimdRasterMeasurement = {
              ok: image.naturalWidth > 0 && image.naturalHeight > 0,
              width: image.naturalWidth,
              height: image.naturalHeight,
              ratio: window.devicePixelRatio || 1,
              complete: image.complete,
              source: (image.currentSrc || image.src || '').slice(0, 200)
            };
          };
          window.wimdRasterMeasurement = null;
          publish();
        })();
        """;

    private readonly string userDataFolder;
    private readonly Dispatcher dispatcher;

    public WebView2ImageRasterizer(string userDataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        this.userDataFolder = userDataFolder;
        dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public async Task<byte[]> RasterizeToPngAsync(
        byte[] imageBytes,
        string fileName,
        int? pixelWidth,
        int? pixelHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "WIMD",
            "Rasterizer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDirectory);
        try
        {
            string imagePath = Path.Combine(scratchDirectory, fileName);
            await File.WriteAllBytesAsync(imagePath, imageBytes, cancellationToken)
                .ConfigureAwait(false);
            return await dispatcher.InvokeAsync(
                () => CaptureAsync(imagePath, pixelWidth, pixelHeight, cancellationToken))
                .Task
                .Unwrap()
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(scratchDirectory);
        }
    }

    private async Task<byte[]> CaptureAsync(
        string imagePath,
        int? pixelWidth,
        int? pixelHeight,
        CancellationToken cancellationToken)
    {
        // Everything, including building the surface, sits inside the guarded region:
        // a failure while creating the offscreen window has to reach the caller as a
        // reportable export error, not escape as an unhandled exception.
        Window? host = null;
        WebView2? view = null;
        try
        {
            host = CreateHostWindow();
            view = new WebView2();
            host.Content = view;
            host.Show();
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder).ConfigureAwait(true);
            await view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            CoreWebView2 core = view.CoreWebView2;
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping(
                RasterHostName,
                Path.GetDirectoryName(imagePath) ?? throw new InvalidOperationException(
                    "光栅化临时目录无效。"),
                CoreWebView2HostResourceAccessKind.DenyCors);

            // Registered before navigating so it runs inside the raster document itself.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(MeasureScript)
                .ConfigureAwait(true);
            core.NavigateToString(BuildRasterDocument(Path.GetFileName(imagePath)));
            (int width, int height, double ratio) = await MeasureAsync(core, cancellationToken)
                .ConfigureAwait(true);

            int captureWidth = pixelWidth ?? width;
            int captureHeight = pixelHeight ?? height;
            (double cssWidth, double cssHeight) = ResolveCaptureSize(
                captureWidth,
                captureHeight,
                ratio);

            // The control size is in device-independent pixels while the capture counts
            // device pixels, so the surface is sized by the ratio. That is what makes
            // the exported bitmap match the requested pixel size on a scaled display.
            host.Width = cssWidth;
            host.Height = cssHeight;
            view.Width = cssWidth;
            view.Height = cssHeight;
            host.UpdateLayout();
            await ApplyImageSizeAsync(core, cssWidth, cssHeight, cancellationToken)
                .ConfigureAwait(true);

            using MemoryStream stream = new();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);
            if (stream.Length is <= 0 or > PreviewImageSaveService.MaximumImageBytes)
            {
                throw new PreviewImageSaveException("PNG 导出结果为空或超过 32 MB。");
            }

            return stream.ToArray();
        }
        catch (PreviewImageSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            throw new PreviewImageSaveException(
                $"无法把图片渲染为 PNG：{exception.Message}",
                exception);
        }
        finally
        {
            TryReleaseRasterSurface(view, host);
        }
    }

    /// <summary>
    /// Releases the offscreen surface on the way out of every path. Cleanup failures are
    /// swallowed on purpose: the process is already holding a result or a reportable
    /// error, and a secondary failure here must not replace it.
    /// </summary>
    private static void TryReleaseRasterSurface(WebView2? view, Window? host)
    {
        try
        {
            view?.Dispose();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
        }

        try
        {
            host?.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private static Window CreateHostWindow() => new()
    {
        Left = OffscreenCoordinate,
        Top = OffscreenCoordinate,
        Width = 32,
        Height = 32,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = false,
        Title = "WIMD 图片导出",

        // A transparent window would have no composited surface to capture.
        AllowsTransparency = false,
    };

    private static async Task<(int Width, int Height, double Ratio)> MeasureAsync(
        CoreWebView2 core,
        CancellationToken cancellationToken)
    {
        string? result = await WebViewScriptResult.PollAsync(
            core,
            "window.wimdRasterMeasurement",
            RasterStepTimeout,
            cancellationToken).ConfigureAwait(true);
        if (result is null || result.Length > MaximumMeasurementLength)
        {
            // The page is asked what it actually became. A slot that never appears has
            // several possible causes and the page state tells them apart.
            throw new PreviewImageSaveException(
                $"离屏渲染页面没有报告图片尺寸。页面状态：{await DescribePageAsync(core).ConfigureAwait(true)}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out JsonElement ok)
                || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("width", out JsonElement widthElement)
                || !widthElement.TryGetInt32(out int width)
                || !root.TryGetProperty("height", out JsonElement heightElement)
                || !heightElement.TryGetInt32(out int height)
                || width <= 0
                || height <= 0)
            {
                // The reply itself is the only clue available when the offscreen page
                // cannot decode the picture, so it is attached to the report instead of
                // collapsing every cause into one message.
                throw new PreviewImageSaveException(
                    $"图片内容无法渲染，不能导出为 PNG。页面回执：{result}");
            }

            double ratio = root.TryGetProperty("ratio", out JsonElement ratioElement)
                && ratioElement.TryGetDouble(out double parsedRatio)
                && parsedRatio > 0
                    ? parsedRatio
                    : 1;
            return (width, height, ratio);
        }
        catch (JsonException exception)
        {
            throw new PreviewImageSaveException("图片尺寸结果无法解析。", exception);
        }
    }

    /// <summary>
    /// Reports what the offscreen page currently is, for diagnostics only. A missing
    /// measurement slot otherwise gives no clue about whether the navigation finished,
    /// whether the document-created script ran, or whether the image was ever found.
    /// </summary>
    private static async Task<string> DescribePageAsync(CoreWebView2 core)
    {
        try
        {
            return (await core.ExecuteScriptAsync("""
                ({
                  readyState: document.readyState,
                  hasImage: !!document.getElementById('image'),
                  slot: String(window.wimdRasterMeasurement),
                  url: location.href.slice(0, 120)
                })
                """).ConfigureAwait(true)).Trim();
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            return $"页面状态不可读：{exception.Message}";
        }
    }

    /// <summary>
    /// Draws the image at the surface size and waits until the page reports that the two
    /// animation frames have been painted, so the capture reads a settled surface.
    /// </summary>
    private static async Task ApplyImageSizeAsync(
        CoreWebView2 core,
        double cssWidth,
        double cssHeight,
        CancellationToken cancellationToken)
    {
        string width = cssWidth.ToString("0.###", CultureInfo.InvariantCulture);
        string height = cssHeight.ToString("0.###", CultureInfo.InvariantCulture);
        string? result = await WebViewScriptResult.RunAsync(
            core,
            $$"""
            (() => {
              window.wimdRasterSized = null;
              const image = document.getElementById('image');
              if (!image) { window.wimdRasterSized = false; return; }
              image.style.width = '{{width}}px';
              image.style.height = '{{height}}px';
              let frames = 0;
              const settle = () => {
                frames += 1;
                if (frames < 2) { requestAnimationFrame(settle); return; }
                window.wimdRasterSized = image.naturalWidth > 0;
              };
              requestAnimationFrame(settle);
            })();
            """,
            "window.wimdRasterSized",
            RasterStepTimeout,
            cancellationToken).ConfigureAwait(true);
        if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException("图片在导出前已被释放。");
        }
    }

    /// <summary>
    /// Converts the requested pixel size into the surface size. The surface is bounded
    /// and never zero, and the ratio is reapplied on capture so a scaled display still
    /// produces the requested number of pixels.
    /// </summary>
    private static (double Width, double Height) ResolveCaptureSize(
        int pixelWidth,
        int pixelHeight,
        double ratio)
    {
        double width = Math.Clamp(pixelWidth, 1, MaximumCaptureDimension);
        double height = Math.Clamp(pixelHeight, 1, MaximumCaptureDimension);
        return (width / ratio, height / ratio);
    }

    private static string BuildRasterDocument(string fileName)
    {
        string source = $"https://{RasterHostName}/{Uri.EscapeDataString(fileName)}";
        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https://{{RasterHostName}}; style-src 'unsafe-inline'; script-src 'none';">
              <style>
                html, body { margin: 0; padding: 0; overflow: hidden; background: transparent; }
                #image { display: block; max-width: none; max-height: none; }
              </style>
            </head>
            <body><img id="image" src="{{source}}" alt="" draggable="false"></body>
            </html>
            """;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The scratch directory holds only the bytes the caller already owns. A
            // WebView2 handle can outlive the capture briefly; leaving it behind is
            // safer than masking the export result.
        }
    }
}
