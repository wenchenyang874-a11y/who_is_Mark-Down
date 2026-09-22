using System.Drawing;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Security;

namespace WhoIsMarkdown.App;

/// <summary>
/// Displays one validated preview image in a modeless, independently resizable
/// window. Host-injected interaction code owns zooming and panning; page-authored
/// script and navigation remain disabled.
///
/// Bug fix 1 (reused viewer): the window is a long-lived instance owned by
/// MainWindow. <see cref="LoadImage"/> swaps the displayed image in place instead of
/// closing and recreating the window, so opening another diagram preserves the
/// user's window position, size, maximized state and taskbar entry.
///
/// Bug fix 2 (default view): the fitted and centered view has to survive two ordering
/// problems. A viewer-cache image can finish decoding before DOMContentLoaded, so
/// completion is checked as well as subscribed, and the WebView2 surface can be laid
/// out after the page loads without raising a window 'resize', so the viewport itself
/// is observed. The magnification behind the reported "huge diagram pinned to the
/// top-left corner" was a separate defect: the SVG carried no intrinsic pixel size, and
/// that is now resolved where the image is materialized (see SvgPixelSizeNormalizer).
///
/// Bug fix 3 (stale pixels): every load maps the image through a fresh, one-shot
/// virtual host name. Chromium otherwise reuses the cached response for an
/// identical origin and file name, which kept showing the previous bytes after the
/// source image or the document had changed on disk.
/// </summary>
public partial class PreviewImageWindow : Window
{
    /// <summary>
    /// One-shot virtual host prefix. A unique host per load is what defeats the
    /// Chromium response cache described in bug fix 3 above.
    /// </summary>
    private const string ViewerHostNamePrefix = "wimd-image-viewer-";

    private const string ViewerInteractionScript = """
        (() => {
          const minimumScale = 0.05;
          const maximumScale = 20;
          let viewport;
          let image;
          let scale = 1;
          let offsetX = 0;
          let offsetY = 0;
          let fitted = true;
          let dragging = false;
          let pointerX = 0;
          let pointerY = 0;

          const clampScale = value => Math.min(maximumScale, Math.max(minimumScale, value));
          const notify = () => window.chrome.webview.postMessage({
            type: 'viewer-state',
            zoomPercent: Math.round(scale * 100),
            width: image?.naturalWidth || 0,
            height: image?.naturalHeight || 0
          });
          const apply = (shouldNotify = true) => {
            image.style.transform = `translate(${offsetX}px, ${offsetY}px) scale(${scale})`;
            if (shouldNotify) notify();
          };
          // The viewport can still report zero while the first layout pass runs, so
          // fall back to the root box instead of fitting against a 1px surface.
          const viewportWidth = () => viewport.clientWidth || document.documentElement.clientWidth || 0;
          const viewportHeight = () => viewport.clientHeight || document.documentElement.clientHeight || 0;
          const centerAtScale = nextScale => {
            scale = clampScale(nextScale);
            offsetX = (viewportWidth() - image.naturalWidth * scale) / 2;
            offsetY = (viewportHeight() - image.naturalHeight * scale) / 2;
            apply();
          };
          const fit = () => {
            if (!image?.naturalWidth || !image?.naturalHeight) return;
            const horizontal = Math.max(1, viewportWidth() - 48) / image.naturalWidth;
            const vertical = Math.max(1, viewportHeight() - 48) / image.naturalHeight;
            fitted = true;
            centerAtScale(Math.min(horizontal, vertical));
          };
          const fitAfterLayout = () => requestAnimationFrame(() => fit());
          const actual = () => {
            fitted = false;
            centerAtScale(1);
          };
          const zoomAt = (factor, clientX, clientY) => {
            if (!image?.naturalWidth) return;
            const rect = viewport.getBoundingClientRect();
            const pointX = clientX - rect.left;
            const pointY = clientY - rect.top;
            const imageX = (pointX - offsetX) / scale;
            const imageY = (pointY - offsetY) / scale;
            const nextScale = clampScale(scale * factor);
            offsetX = pointX - imageX * nextScale;
            offsetY = pointY - imageY * nextScale;
            scale = nextScale;
            fitted = false;
            apply();
          };
          const zoomFromCenter = factor => {
            const rect = viewport.getBoundingClientRect();
            zoomAt(factor, rect.left + rect.width / 2, rect.top + rect.height / 2);
          };

          addEventListener('DOMContentLoaded', () => {
            viewport = document.getElementById('viewport');
            image = document.getElementById('image');
            image.draggable = false;
            image.addEventListener('dragstart', event => event.preventDefault());
            // Bug fix 2: a viewer-cache image commonly completes before
            // DOMContentLoaded, and a 'load' listener registered afterwards never
            // fires. Detect that case up front, otherwise the viewer stays at 100%
            // in the top-left corner.
            if (image.complete && image.naturalWidth > 0) {
              fitAfterLayout();
            } else {
              image.addEventListener('load', fitAfterLayout, { once: true });
            }
            image.addEventListener('error', () => window.chrome.webview.postMessage({ type: 'viewer-error' }));

            viewport.addEventListener('wheel', event => {
              event.preventDefault();
              zoomAt(event.deltaY < 0 ? 1.12 : 1 / 1.12, event.clientX, event.clientY);
            }, { passive: false });
            viewport.addEventListener('pointerdown', event => {
              if (event.button !== 0) return;
              event.preventDefault();
              dragging = true;
              pointerX = event.clientX;
              pointerY = event.clientY;
              viewport.classList.add('is-dragging');
              viewport.setPointerCapture(event.pointerId);
            });
            viewport.addEventListener('pointermove', event => {
              if (!dragging) return;
              event.preventDefault();
              offsetX += event.clientX - pointerX;
              offsetY += event.clientY - pointerY;
              pointerX = event.clientX;
              pointerY = event.clientY;
              fitted = false;
              apply(false);
            });
            const finishDrag = event => {
              if (!dragging) return;
              dragging = false;
              viewport.classList.remove('is-dragging');
              if (viewport.hasPointerCapture(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
            };
            viewport.addEventListener('pointerup', finishDrag);
            viewport.addEventListener('pointercancel', finishDrag);
            viewport.addEventListener('dblclick', () => fitted ? actual() : fit());
            addEventListener('resize', () => { if (fitted) fit(); });
            // The WebView2 surface can receive its real size after the document has
            // loaded, and resizing that surface does not always raise a window
            // 'resize'. Observing the viewport itself re-fits once the area is known
            // instead of leaving whatever the first, possibly empty, layout produced.
            if (typeof ResizeObserver === 'function') {
              new ResizeObserver(() => { if (fitted) fit(); }).observe(viewport);
            }
          });

          window.wimdImageViewer = {
            fit,
            actual,
            zoomIn: () => zoomFromCenter(1.2),
            zoomOut: () => zoomFromCenter(1 / 1.2)
          };
        })();
        """;

    private PreparedPreviewImage preparedImage;
    private readonly PreviewNavigationGate navigationGate = new();

    /// <summary>
    /// Save-as entries shared by the toolbar button and the right-click menu, so both
    /// surfaces offer exactly the same formats for the same picture. The entries are
    /// supplied by the desktop layer because it owns the export rules and the
    /// destination dialog.
    /// </summary>
    private IReadOnlyList<PreviewImageExportOption> exportOptions = [];

    /// <summary>
    /// Entries of the right-click menu that is currently on screen. WebView2 only
    /// raises CustomItemSelected for items the host still references.
    /// </summary>
    private readonly List<CoreWebView2ContextMenuItem> contextMenuItems = [];

    private CoreWebView2? core;

    /// <summary>
    /// Virtual host that serves the current cache directory. It is replaced on every
    /// load so a re-displayed image can never be answered from Chromium's cache.
    /// </summary>
    private string? viewerHostName;
    private bool initialized;
    private bool initializationFailed;
    private bool closed;

    public PreviewImageWindow(PreparedPreviewImage preparedImage, string? alternativeText)
    {
        this.preparedImage = preparedImage ?? throw new ArgumentNullException(nameof(preparedImage));
        InitializeComponent();
        ApplyImageIdentity(alternativeText);
        Loaded += Window_Loaded;
    }

    /// <summary>
    /// Raised when the user picks a save-as format. The dialog and the file access stay
    /// with the desktop layer, which owns the export rules and the error reporting.
    /// </summary>
    public event EventHandler<PreviewImageExportRequestedEventArgs>? ExportRequested;

    public PreparedPreviewImage PreparedImage => preparedImage;

    /// <summary>
    /// Publishes the save-as entries for the displayed image. Called whenever the
    /// window takes over another picture, because the available formats depend on the
    /// encoding: a vector diagram can be exported as SVG while a bitmap cannot.
    /// </summary>
    public void SetExportOptions(IReadOnlyList<PreviewImageExportOption> options)
    {
        exportOptions = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// True while this window can still take over another image. A viewer that
    /// failed to initialize or is already closing cannot, and the caller must open
    /// a fresh window instead of leaving the user with a dead surface.
    /// </summary>
    public bool CanLoadAnotherImage => !closed && !initializationFailed;

    /// <summary>
    /// Reuses the open window for another preview image. A viewer that has already
    /// been initialized navigates in place; one that is still starting up only
    /// records the newest image so its first navigation is not stale.
    /// </summary>
    /// <returns>
    /// The image that was replaced, or <see langword="null"/> when nothing changed
    /// or this window cannot be reused. The caller owns that image's cache directory
    /// and releases it afterwards.
    /// </returns>
    public PreparedPreviewImage? LoadImage(
        PreparedPreviewImage image,
        string? alternativeText)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (ReferenceEquals(preparedImage, image) || !CanLoadAnotherImage)
        {
            return null;
        }

        PreparedPreviewImage previous = preparedImage;
        preparedImage = image;
        ApplyImageIdentity(alternativeText);

        if (core is null)
        {
            // Initialization is still running. The first navigation reads
            // preparedImage, so it already renders this newest image.
            return previous;
        }

        try
        {
            NavigateToPreparedImage();
        }
        catch (Exception exception) when (IsViewerFailure(exception))
        {
            // Keep the previous image and its cache so the window is not left in a
            // half-switched state when the new navigation cannot start.
            preparedImage = previous;
            throw;
        }

        return previous;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        Loaded -= Window_Loaded;
        if (core is not null)
        {
            core.NavigationStarting -= Core_NavigationStarting;
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.WebMessageReceived -= Core_WebMessageReceived;
            core.ContextMenuRequested -= Core_ContextMenuRequested;
            contextMenuItems.Clear();
            if (viewerHostName is not null)
            {
                core.ClearVirtualHostNameToFolderMapping(viewerHostName);
            }

            core = null;
        }

        ImageWebView.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        if (initialized)
        {
            return;
        }

        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WIMD",
                "WebView2");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            await ImageWebView.EnsureCoreWebView2Async(environment);

            // The window can be closed while the environment is still starting.
            if (closed)
            {
                return;
            }

            core = ImageWebView.CoreWebView2;
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ViewerInteractionScript);
            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.WebMessageReceived += Core_WebMessageReceived;
            // Default context menus stay enabled: WIMD replaces the entries through the
            // ContextMenuRequested handler instead of suppressing the menu, which keeps
            // the same code path working if a request ever arrives without our items.
            core.ContextMenuRequested += Core_ContextMenuRequested;

            NavigateToPreparedImage();
            initialized = true;
        }
        catch (Exception exception) when (IsViewerFailure(exception))
        {
            initializationFailed = true;
            DimensionsText.Text = $"图片加载失败：{exception.Message}";
        }
    }

    /// <summary>
    /// Points the one-shot virtual host at the current cache directory and loads the
    /// generated viewer document for <see cref="preparedImage"/>.
    /// </summary>
    private void NavigateToPreparedImage()
    {
        if (core is null)
        {
            return;
        }

        string imageDirectory = Path.GetDirectoryName(preparedImage.FilePath)
            ?? throw new InvalidOperationException("图片查看器缓存路径无效。");
        string hostName = string.Concat(ViewerHostNamePrefix, Guid.NewGuid().ToString("N"));
        core.SetVirtualHostNameToFolderMapping(
            hostName,
            imageDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        if (viewerHostName is not null)
        {
            // Release the previous mapping only after the replacement succeeded so a
            // failed load cannot leave the viewer without a reachable host.
            core.ClearVirtualHostNameToFolderMapping(viewerHostName);
        }

        viewerHostName = hostName;
        ResetViewerState();
        navigationGate.BeginGeneratedNavigation();
        core.NavigateToString(BuildViewerDocument(hostName));
    }

    private void ResetViewerState()
    {
        ZoomText.Text = "--";
        DimensionsText.Text = "正在载入…";
        SetViewerCommandsEnabled(false);
    }

    private void ApplyImageIdentity(string? alternativeText)
    {
        string displayName = string.IsNullOrWhiteSpace(alternativeText)
            ? Path.GetFileNameWithoutExtension(preparedImage.SuggestedFileName)
            : alternativeText.Trim();
        ImageNameText.Text = displayName;
        Title = $"{displayName} - WIMD 图片预览";
    }

    private string BuildViewerDocument(string hostName)
    {
        string fileName = Uri.EscapeDataString(Path.GetFileName(preparedImage.FilePath));
        string source = $"https://{hostName}/{fileName}";
        string title = WebUtility.HtmlEncode(ImageNameText.Text);
        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https://{{hostName}}; style-src 'unsafe-inline'; script-src 'none';">
              <title>{{title}}</title>
              <style>
                html, body, #viewport { width: 100%; height: 100%; margin: 0; overflow: hidden; }
                body { background: #17181d; }
                #viewport {
                  position: relative;
                  cursor: grab;
                  touch-action: none;
                  user-select: none;
                  background-color: #202127;
                  background-image: linear-gradient(45deg, #272930 25%, transparent 25%), linear-gradient(-45deg, #272930 25%, transparent 25%), linear-gradient(45deg, transparent 75%, #272930 75%), linear-gradient(-45deg, transparent 75%, #272930 75%);
                  background-position: 0 0, 0 12px, 12px -12px, -12px 0;
                  background-size: 24px 24px;
                }
                #viewport.is-dragging { cursor: grabbing; }
                #image {
                  position: absolute;
                  top: 0;
                  left: 0;
                  max-width: none;
                  max-height: none;
                  transform-origin: 0 0;
                  pointer-events: none;
                  user-select: none;
                  -webkit-user-drag: none;
                  box-shadow: 0 18px 54px rgba(0, 0, 0, 0.36);
                }
              </style>
            </head>
            <body><div id="viewport"><img id="image" src="{{source}}" alt="{{title}}" draggable="false"></div></body>
            </html>
            """;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!navigationGate.TryAllowGeneratedNavigation(eventArgs.Uri))
        {
            eventArgs.Cancel = true;
        }
    }

    private static void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
    }

    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            JsonElement root = message.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;
            if (type == "viewer-error")
            {
                DimensionsText.Text = "图片加载失败";
                return;
            }

            if (type != "viewer-state")
            {
                return;
            }

            int zoom = root.TryGetProperty("zoomPercent", out JsonElement zoomElement)
                && zoomElement.TryGetInt32(out int zoomValue)
                ? zoomValue
                : 100;
            int width = root.TryGetProperty("width", out JsonElement widthElement)
                && widthElement.TryGetInt32(out int widthValue)
                ? widthValue
                : 0;
            int height = root.TryGetProperty("height", out JsonElement heightElement)
                && heightElement.TryGetInt32(out int heightValue)
                ? heightValue
                : 0;
            ZoomText.Text = $"{zoom}%";
            DimensionsText.Text = width > 0 && height > 0 ? $"{width} × {height}" : "正在载入…";
            SetViewerCommandsEnabled(width > 0 && height > 0);
        }
        catch (JsonException)
        {
            // Only the host-injected viewer script can post messages. Ignore malformed
            // input instead of allowing it to affect the desktop event loop.
        }
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteViewerCommandAsync("zoomOut");
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteViewerCommandAsync("zoomIn");
    }

    private async void Fit_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteViewerCommandAsync("fit");
    }

    private async void ActualSize_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteViewerCommandAsync("actual");
    }

    /// <summary>
    /// Opens the save-as entries under the toolbar button. The button used to save a
    /// single implicit format, which is what made it disagree with the right-click menu.
    /// </summary>
    private void SaveAs_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (exportOptions.Count == 0)
        {
            return;
        }

        ContextMenu menu = new()
        {
            PlacementTarget = SaveAsButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (PreviewImageExportOption option in exportOptions)
        {
            MenuItem item = new() { Header = option.Label };
            PreviewImageExportFormat format = option.Format;
            item.Click += (_, _) => RequestExport(format);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Replaces WebView2's default menu with the same save-as entries the toolbar
    /// offers. The browser menu could only save the generated host document as HTML,
    /// which is why right-clicking a diagram saved a page instead of the diagram.
    /// </summary>
    private void Core_ContextMenuRequested(
        object? sender,
        CoreWebView2ContextMenuRequestedEventArgs eventArgs)
    {
        CoreWebView2? currentCore = core;
        if (currentCore is null || exportOptions.Count == 0)
        {
            return;
        }

        contextMenuItems.Clear();
        foreach (PreviewImageExportOption option in exportOptions)
        {
            CoreWebView2ContextMenuItem item = currentCore.Environment.CreateContextMenuItem(
                $"另存为 {option.Label}",
                null,
                CoreWebView2ContextMenuItemKind.Command);
            PreviewImageExportFormat format = option.Format;
            item.CustomItemSelected += (_, _) => RequestExport(format);
            contextMenuItems.Add(item);
        }

        // The whole viewer surface is the picture, so the browser menu has nothing to
        // contribute here and is replaced rather than extended.
        eventArgs.MenuItems.Clear();
        foreach (CoreWebView2ContextMenuItem item in contextMenuItems)
        {
            eventArgs.MenuItems.Add(item);
        }
    }

    private void RequestExport(PreviewImageExportFormat format) =>
        ExportRequested?.Invoke(this, new PreviewImageExportRequestedEventArgs(preparedImage, format));

    private async Task ExecuteViewerCommandAsync(string command)
    {
        if (core is null)
        {
            return;
        }

        await core.ExecuteScriptAsync($"window.wimdImageViewer?.{command}();");
    }

    private void SetViewerCommandsEnabled(bool enabled)
    {
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        ActualSizeButton.IsEnabled = enabled;
        SaveAsButton.IsEnabled = enabled;
    }

    private static bool IsViewerFailure(Exception exception) => exception is
        InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or System.Runtime.InteropServices.COMException;
}
