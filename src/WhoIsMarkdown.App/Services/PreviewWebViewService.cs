using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Security;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Configures WebView2 as an offline preview surface. Only the current document
/// directory is exposed through an isolated virtual host, and host-injected scripts
/// provide scroll synchronization without enabling page-authored JavaScript.
/// </summary>
public sealed class PreviewWebViewService : IDisposable
{
    private const string ScrollReportingScript = """
        (() => {
          let scheduled = false;
          addEventListener('scroll', () => {
            if (scheduled) return;
            scheduled = true;
            requestAnimationFrame(() => {
              scheduled = false;
              const root = document.scrollingElement || document.documentElement;
              const maximum = Math.max(0, root.scrollHeight - root.clientHeight);
              window.chrome.webview.postMessage({
                type: 'scroll',
                ratio: maximum === 0 ? 0 : root.scrollTop / maximum
              });
            });
          }, { passive: true });
        })();
        """;

    private readonly WebView2 webView;
    private readonly PreviewNavigationGate navigationGate = new();
    private CoreWebView2? core;
    private string? mappedDocumentDirectory;
    private bool initialized;
    private bool disposed;

    public PreviewWebViewService(WebView2 webView)
    {
        this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
        this.webView.DefaultBackgroundColor = Color.Transparent;
    }

    public event EventHandler<string>? ExternalNavigationFailed;

    public event EventHandler<string>? PreviewNavigationFailed;

    public event EventHandler<PreviewScrollChangedEventArgs>? ScrollRatioChanged;

    public event EventHandler? PreviewReady;

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (initialized)
        {
            return;
        }

        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WIMD",
            "WebView2");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder).ConfigureAwait(true);
        await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        ThrowIfDisposed();

        core = webView.CoreWebView2;
        // Bug fix: WebView2 does not execute AddScriptToExecuteOnDocumentCreated
        // registrations while this flag is false, which broke preview-to-editor
        // scroll reporting. Page-authored scripts remain blocked independently:
        // Markdown disables raw HTML and the generated page uses script-src 'none'.
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScrollReportingScript).ConfigureAwait(true);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        initialized = true;
    }

    public void Show(string html, string? documentPath)
    {
        ArgumentNullException.ThrowIfNull(html);
        ThrowIfDisposed();

        if (!initialized || core is null)
        {
            throw new InvalidOperationException("预览组件尚未初始化。");
        }

        ConfigureDocumentResourceMapping(documentPath);

        // Bug fix: WebView2 represents NavigateToString as a data:text/html
        // navigation. A single-use gate admits only this host-initiated document,
        // while user navigation to arbitrary data URLs remains blocked.
        navigationGate.BeginGeneratedNavigation();
        try
        {
            core.NavigateToString(html);
        }
        catch
        {
            navigationGate.CancelGeneratedNavigation();
            throw;
        }
    }

    public Task ScrollToRatioAsync(double ratio)
    {
        double clampedRatio = Math.Clamp(ratio, 0, 1);
        string invariantRatio = clampedRatio.ToString("R", CultureInfo.InvariantCulture);
        return ExecuteHostScriptAsync(
            $"window.scrollTo(0, Math.max(0, document.documentElement.scrollHeight - window.innerHeight) * {invariantRatio});");
    }

    /// <summary>
    /// Keeps the source anchor inside the middle 25%-75% of the visible preview.
    /// A caret move already inside that comfort zone is a no-op; an anchor outside
    /// it is moved only to the nearest boundary, avoiding the old jump-to-top effect.
    /// </summary>
    public Task EnsureSourceLineInComfortZoneAsync(int zeroBasedLine)
    {
        int safeLine = Math.Max(0, zeroBasedLine);
        return ExecuteHostScriptAsync($$"""
            (() => {
              const line = {{safeLine}};
              const anchors = [...document.querySelectorAll('[id^="pragma-line-"]')]
                .map(element => ({ element, line: Number(element.id.slice(12)) }))
                .filter(item => Number.isFinite(item.line));
              if (anchors.length === 0) return;
              let target = anchors[0];
              for (const candidate of anchors) {
                if (candidate.line > line) break;
                target = candidate;
              }

              const viewportHeight = Math.max(1, window.innerHeight);
              const anchorTop = target.element.getBoundingClientRect().top;
              const comfortTop = viewportHeight * 0.25;
              const comfortBottom = viewportHeight * 0.75;
              if (anchorTop >= comfortTop && anchorTop <= comfortBottom) return;

              const nearestBoundary = anchorTop < comfortTop ? comfortTop : comfortBottom;
              const absoluteTop = anchorTop + window.scrollY;
              window.scrollTo(0, Math.max(0, absoluteTop - nearestBoundary));
            })();
            """);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        navigationGate.CancelGeneratedNavigation();
        if (core is not null)
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.NewWindowRequested -= OnNewWindowRequested;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ClearVirtualHostNameToFolderMapping(LocalImageUrlResolver.VirtualHostName);
            core = null;
        }

        webView.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ExecuteHostScriptAsync(string script)
    {
        ThrowIfDisposed();
        if (!initialized || core is null)
        {
            return;
        }

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);
    }

    /// <summary>
    /// Bug fix: file URLs from NavigateToString are not a reliable base for relative
    /// images. The current Markdown directory is mapped to an isolated HTTPS origin;
    /// DenyCors permits image elements but blocks fetch/XHR access.
    /// </summary>
    private void ConfigureDocumentResourceMapping(string? documentPath)
    {
        if (core is null)
        {
            return;
        }

        string? directory = string.IsNullOrWhiteSpace(documentPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(documentPath));
        if (string.Equals(directory, mappedDocumentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        core.ClearVirtualHostNameToFolderMapping(LocalImageUrlResolver.VirtualHostName);
        mappedDocumentDirectory = null;
        if (directory is null)
        {
            return;
        }

        core.SetVirtualHostNameToFolderMapping(
            LocalImageUrlResolver.VirtualHostName,
            directory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        mappedDocumentDirectory = directory;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (navigationGate.TryAllowGeneratedNavigation(eventArgs.Uri))
        {
            return;
        }

        eventArgs.Cancel = true;
        TryOpenExternalUri(eventArgs.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess && eventArgs.WebErrorStatus is not CoreWebView2WebErrorStatus.OperationCanceled)
        {
            PreviewNavigationFailed?.Invoke(this, $"预览页面加载失败：{eventArgs.WebErrorStatus}");
            return;
        }

        if (eventArgs.IsSuccess)
        {
            PreviewReady?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (root.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "scroll"
                && root.TryGetProperty("ratio", out JsonElement ratio)
                && ratio.TryGetDouble(out double value))
            {
                ScrollRatioChanged?.Invoke(this, new PreviewScrollChangedEventArgs(value));
            }
        }
        catch (JsonException)
        {
            // Messages are emitted only by the host-injected script. Invalid data is
            // ignored defensively rather than affecting the editor event loop.
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        TryOpenExternalUri(eventArgs.Uri);
    }

    private void TryOpenExternalUri(string rawUri)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out Uri? uri) || !IsAllowedExternalScheme(uri.Scheme))
        {
            ExternalNavigationFailed?.Invoke(this, $"已阻止不安全或无效的链接：{Abbreviate(rawUri)}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ExternalNavigationFailed?.Invoke(this, $"无法打开外部链接：{exception.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string Abbreviate(string value)
    {
        const int maximumDisplayLength = 120;
        return value.Length <= maximumDisplayLength
            ? value
            : string.Concat(value.AsSpan(0, maximumDisplayLength), "…");
    }

    private static bool IsAllowedExternalScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
