using System.Drawing;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Security;

namespace WhoIsMarkdown.App;

/// <summary>
/// Displays one validated preview image in a modeless, independently resizable
/// window. Host-injected interaction code owns zooming and panning; page-authored
/// script and navigation remain disabled.
/// </summary>
public partial class PreviewImageWindow : Window
{
    private const string ViewerHostName = "wimd-image-viewer.invalid";

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
          const centerAtScale = nextScale => {
            scale = clampScale(nextScale);
            offsetX = (viewport.clientWidth - image.naturalWidth * scale) / 2;
            offsetY = (viewport.clientHeight - image.naturalHeight * scale) / 2;
            apply();
          };
          const fit = () => {
            if (!image?.naturalWidth || !image?.naturalHeight) return;
            const horizontal = Math.max(1, viewport.clientWidth - 48) / image.naturalWidth;
            const vertical = Math.max(1, viewport.clientHeight - 48) / image.naturalHeight;
            fitted = true;
            centerAtScale(Math.min(horizontal, vertical));
          };
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
            image.addEventListener('load', fit, { once: true });
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
          });

          window.wimdImageViewer = {
            fit,
            actual,
            zoomIn: () => zoomFromCenter(1.2),
            zoomOut: () => zoomFromCenter(1 / 1.2)
          };
        })();
        """;

    private readonly PreparedPreviewImage preparedImage;
    private readonly PreviewNavigationGate navigationGate = new();
    private CoreWebView2? core;
    private bool initialized;

    public PreviewImageWindow(PreparedPreviewImage preparedImage, string? alternativeText)
    {
        this.preparedImage = preparedImage ?? throw new ArgumentNullException(nameof(preparedImage));
        InitializeComponent();

        string displayName = string.IsNullOrWhiteSpace(alternativeText)
            ? Path.GetFileNameWithoutExtension(preparedImage.SuggestedFileName)
            : alternativeText.Trim();
        ImageNameText.Text = displayName;
        Title = $"{displayName} - WIMD 图片预览";
        Loaded += Window_Loaded;
    }

    public event EventHandler? SaveRequested;

    public PreparedPreviewImage PreparedImage => preparedImage;

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= Window_Loaded;
        if (core is not null)
        {
            core.NavigationStarting -= Core_NavigationStarting;
            core.NewWindowRequested -= Core_NewWindowRequested;
            core.WebMessageReceived -= Core_WebMessageReceived;
            core.ClearVirtualHostNameToFolderMapping(ViewerHostName);
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
            string imageDirectory = Path.GetDirectoryName(preparedImage.FilePath)
                ?? throw new InvalidOperationException("图片查看器缓存路径无效。");
            core.SetVirtualHostNameToFolderMapping(
                ViewerHostName,
                imageDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);

            navigationGate.BeginGeneratedNavigation();
            core.NavigateToString(BuildViewerDocument());
            initialized = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            DimensionsText.Text = $"图片加载失败：{exception.Message}";
        }
    }

    private string BuildViewerDocument()
    {
        string fileName = Uri.EscapeDataString(Path.GetFileName(preparedImage.FilePath));
        string source = $"https://{ViewerHostName}/{fileName}";
        string title = WebUtility.HtmlEncode(ImageNameText.Text);
        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https://{{ViewerHostName}}; style-src 'unsafe-inline'; script-src 'none';">
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

    private void SaveAs_Click(object sender, RoutedEventArgs eventArgs)
    {
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

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
}
