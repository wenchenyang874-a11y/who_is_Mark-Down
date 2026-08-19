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

    private const string ImageInteractionScript = """
        (() => {
          const imageSelector = 'main.preview-document img';
          const requestOpen = image => {
            const source = image.currentSrc || image.src;
            if (!source) return;
            window.chrome.webview.postMessage({
              type: 'open-preview-image',
              source,
              alternativeText: image.alt || ''
            });
          };

          const prepareImages = () => {
            document.querySelectorAll(imageSelector).forEach(image => {
              image.draggable = false;
              if (!image.hasAttribute('tabindex')) image.tabIndex = 0;
              if (!image.hasAttribute('role')) image.setAttribute('role', 'button');
              if (!image.hasAttribute('aria-label')) {
                image.setAttribute('aria-label', `${image.alt?.trim() || '图片'}，在独立窗口中查看`);
              }
              if (!image.hasAttribute('title')) image.title = '单击在独立窗口中查看';
            });
          };

          document.addEventListener('click', event => {
            if (!(event.target instanceof Element) || event.button !== 0) return;
            const image = event.target.closest(imageSelector);
            if (!image) return;
            event.preventDefault();
            event.stopPropagation();
            requestOpen(image);
          }, true);

          document.addEventListener('dragstart', event => {
            if (event.target instanceof Element && event.target.closest(imageSelector)) {
              event.preventDefault();
            }
          }, true);

          document.addEventListener('keydown', event => {
            if ((event.key === 'Enter' || event.key === ' ')
                && event.target instanceof Element
                && event.target.matches(imageSelector)) {
              event.preventDefault();
              requestOpen(event.target);
            }
          });

          document.addEventListener('DOMContentLoaded', prepareImages, { once: true });
          document.addEventListener('wimd:preview-updated', prepareImages);
        })();
        """;

    private readonly WebView2 webView;
    private readonly PreviewNavigationGate navigationGate = new();
    private readonly PreviewResourceMappingState resourceMappingState = new();
    private TaskCompletionSource<bool> previewReadySource = CreatePreviewReadySource();
    private CoreWebView2? core;
    private PreviewSnapshot? pendingPreview;
    private bool processingPreview;
    private bool navigationInProgress;
    private bool previewPageReady;
    private bool initialized;
    private bool disposed;
    private string? currentPagePolicyIdentity;

    public PreviewWebViewService(WebView2 webView)
    {
        this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
        this.webView.DefaultBackgroundColor = Color.Transparent;
    }

    public event EventHandler<string>? ExternalNavigationFailed;

    public event EventHandler<string>? PreviewNavigationFailed;

    public event EventHandler<PreviewImageOpenRequestedEventArgs>? PreviewImageOpenRequested;

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
        // scroll reporting. Page-authored scripts remain blocked independently by
        // the HTML allowlist and the generated page's script-src 'none' policy.
        core.Settings.IsScriptEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ScrollReportingScript).ConfigureAwait(true);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ImageInteractionScript).ConfigureAwait(true);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        initialized = true;
    }

    /// <summary>
    /// Updates the existing preview DOM after the first navigation. Reusing the
    /// loaded page avoids WebView2's white unload/repaint frame on every keystroke.
    /// Requests arriving during initial navigation are coalesced to the newest body.
    /// </summary>
    public Task ShowAsync(
        string fullHtml,
        string bodyHtml,
        string? documentPath,
        string pagePolicyIdentity)
    {
        ArgumentNullException.ThrowIfNull(fullHtml);
        ArgumentNullException.ThrowIfNull(bodyHtml);
        ArgumentNullException.ThrowIfNull(pagePolicyIdentity);
        ThrowIfDisposed();

        if (!initialized || core is null)
        {
            throw new InvalidOperationException("预览组件尚未初始化。");
        }

        MarkPreviewPending();
        pendingPreview = new PreviewSnapshot(
            fullHtml,
            bodyHtml,
            documentPath,
            pagePolicyIdentity);
        return ProcessPreviewQueueAsync();
    }

    public async Task ExportPdfAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ThrowIfDisposed();
        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(true);
        if (core is null)
        {
            throw new InvalidOperationException("预览组件尚未初始化。");
        }

        // Remote images can finish after the DOM update. Bound the wait so a dead
        // image host cannot make PDF export hang indefinitely.
        await core.ExecuteScriptAsync("""
            (async () => {
              const pending = [...document.images]
                .filter(image => !image.complete)
                .map(image => new Promise(resolve => {
                  image.addEventListener('load', resolve, { once: true });
                  image.addEventListener('error', resolve, { once: true });
                }));
              await Promise.race([
                Promise.all(pending),
                new Promise(resolve => setTimeout(resolve, 3000))
              ]);
              return true;
            })();
            """).WaitAsync(cancellationToken).ConfigureAwait(true);

        bool succeeded = await core.PrintToPdfAsync(outputPath)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        if (!succeeded)
        {
            throw new InvalidOperationException("WebView2 未能生成 PDF 文件。");
        }
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (previewPageReady
            && !navigationInProgress
            && !processingPreview
            && pendingPreview is null)
        {
            return Task.CompletedTask;
        }

        return previewReadySource.Task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CreatePreviewReadySource()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void MarkPreviewPending()
    {
        if (previewReadySource.Task.IsCompleted)
        {
            previewReadySource = CreatePreviewReadySource();
        }
    }

    private void SignalPreviewReady()
    {
        previewReadySource.TrySetResult(true);
        PreviewReady?.Invoke(this, EventArgs.Empty);
    }

    private void SignalPreviewFailure(string message)
    {
        previewReadySource.TrySetException(new InvalidOperationException(message));
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
        previewReadySource.TrySetCanceled();
        pendingPreview = null;
        previewPageReady = false;
        navigationInProgress = false;
        navigationGate.CancelGeneratedNavigation();
        currentPagePolicyIdentity = null;
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

    /// <summary>
    /// Serializes preview mutations on the UI thread and collapses bursts to the
    /// newest snapshot. A full navigation is used only while no preview page exists.
    /// </summary>
    private async Task ProcessPreviewQueueAsync()
    {
        if (processingPreview || disposed)
        {
            return;
        }

        processingPreview = true;
        try
        {
            while (pendingPreview is not null)
            {
                PreviewSnapshot snapshot = pendingPreview;
                pendingPreview = null;
                bool resourceMappingChanged = ConfigureDocumentResourceMapping(
                    snapshot.DocumentPath);
                bool pagePolicyChanged = !string.Equals(
                    currentPagePolicyIdentity,
                    snapshot.PagePolicyIdentity,
                    StringComparison.Ordinal);
                currentPagePolicyIdentity = snapshot.PagePolicyIdentity;

                // Bug fix: opening a file from an already-running blank editor changes
                // the virtual-host folder after the preview page exists. A DOM-only
                // replacement can keep WebView2's earlier failed image responses, so
                // rebuild the host page once when the resource directory changes.
                if (!previewPageReady || resourceMappingChanged || pagePolicyChanged)
                {
                    NavigateToPreview(snapshot.FullHtml);
                    return;
                }

                bool updated = await TryUpdateBodyAsync(snapshot.BodyHtml).ConfigureAwait(true);
                if (!updated)
                {
                    previewPageReady = false;
                    NavigateToPreview(snapshot.FullHtml);
                    return;
                }
            }

            SignalPreviewReady();
        }
        finally
        {
            // Navigation completion resumes the queue. Keeping this flag set while
            // navigating prevents concurrent calls from starting duplicate pages.
            if (!navigationInProgress)
            {
                processingPreview = false;
            }
        }
    }

    private void NavigateToPreview(string html)
    {
        if (core is null)
        {
            throw new InvalidOperationException("预览组件尚未初始化。");
        }

        // NavigateToString appears as data:text/html. The one-shot navigation gate
        // admits only this host-generated document and keeps user data URLs blocked.
        navigationGate.BeginGeneratedNavigation();
        navigationInProgress = true;
        previewPageReady = false;
        try
        {
            core.NavigateToString(html);
        }
        catch
        {
            navigationInProgress = false;
            navigationGate.CancelGeneratedNavigation();
            currentPagePolicyIdentity = null;
            throw;
        }
    }

    private async Task<bool> TryUpdateBodyAsync(string bodyHtml)
    {
        if (core is null)
        {
            return false;
        }

        string result = await core.ExecuteScriptAsync(PreviewUpdateScriptBuilder.Build(bodyHtml))
            .ConfigureAwait(true);
        return string.Equals(result, "true", StringComparison.Ordinal);
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
    private bool ConfigureDocumentResourceMapping(string? documentPath)
    {
        if (core is null)
        {
            return false;
        }

        PreviewResourceMappingUpdate update = resourceMappingState.Update(documentPath);
        if (!update.HasChanged)
        {
            return false;
        }

        core.ClearVirtualHostNameToFolderMapping(LocalImageUrlResolver.VirtualHostName);
        if (update.DirectoryPath is null)
        {
            return true;
        }

        core.SetVirtualHostNameToFolderMapping(
            LocalImageUrlResolver.VirtualHostName,
            update.DirectoryPath,
            CoreWebView2HostResourceAccessKind.DenyCors);
        return true;
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

    private async void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        // Canceled external navigations are unrelated to the host preview document.
        if (!navigationInProgress)
        {
            return;
        }

        navigationInProgress = false;
        processingPreview = false;
        previewPageReady = eventArgs.IsSuccess;

        if (!eventArgs.IsSuccess)
        {
            if (eventArgs.WebErrorStatus is not CoreWebView2WebErrorStatus.OperationCanceled)
            {
                PreviewNavigationFailed?.Invoke(
                    this,
                    $"预览页面加载失败：{eventArgs.WebErrorStatus}");
            }

            if (pendingPreview is not null)
            {
                try
                {
                    await ProcessPreviewQueueAsync().ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
                {
                    string message = $"预览更新失败：{exception.Message}";
                    SignalPreviewFailure(message);
                    PreviewNavigationFailed?.Invoke(this, message);
                }
            }
            else
            {
                SignalPreviewFailure($"预览页面加载失败：{eventArgs.WebErrorStatus}");
            }

            return;
        }

        try
        {
            if (pendingPreview is not null)
            {
                await ProcessPreviewQueueAsync().ConfigureAwait(true);
            }
            else
            {
                SignalPreviewReady();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            string message = $"预览更新失败：{exception.Message}";
            SignalPreviewFailure(message);
            PreviewNavigationFailed?.Invoke(this, message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using JsonDocument message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            JsonElement root = message.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type))
            {
                return;
            }

            string? messageType = type.GetString();
            if (messageType == "scroll")
            {
                TryRaiseScrollChanged(root);
            }
            else if (messageType == "open-preview-image")
            {
                TryRaisePreviewImageOpenRequested(root);
            }
        }
        catch (JsonException)
        {
            // Messages are emitted only by the host-injected script. Invalid data is
            // ignored defensively rather than affecting the editor event loop.
        }
    }

    private void TryRaiseScrollChanged(JsonElement root)
    {
        if (root.TryGetProperty("ratio", out JsonElement ratio)
            && ratio.TryGetDouble(out double value))
        {
            ScrollRatioChanged?.Invoke(this, new PreviewScrollChangedEventArgs(value));
        }
    }

    private void TryRaisePreviewImageOpenRequested(JsonElement root)
    {
        if (!root.TryGetProperty("source", out JsonElement sourceElement))
        {
            return;
        }

        string? source = sourceElement.GetString();
        if (string.IsNullOrWhiteSpace(source) || source.Length > 48 * 1024 * 1024)
        {
            return;
        }

        string? alternativeText = root.TryGetProperty(
            "alternativeText",
            out JsonElement alternativeTextElement)
            ? alternativeTextElement.GetString()
            : null;
        PreviewImageOpenRequested?.Invoke(
            this,
            new PreviewImageOpenRequestedEventArgs(source, alternativeText));
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

    private sealed record PreviewSnapshot(
        string FullHtml,
        string BodyHtml,
        string? DocumentPath,
        string PagePolicyIdentity);

    private static bool IsAllowedExternalScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
