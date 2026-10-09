using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WhoIsMarkdown.Core.Images;
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
    private const string DocumentImageResourcePattern =
        "https://wimd-document.invalid/*";

    /// <summary>
    /// Longest accepted image source. A generated diagram travels as a base64 data uri,
    /// so the bound is generous but still protects the desktop event loop from an
    /// unbounded payload.
    /// </summary>
    private const int MaximumImageSourceLength = 48 * 1024 * 1024;

    /// <summary>
    /// Longest accepted reply from the element lookup. The reply carries one source url
    /// plus a little metadata, so anything larger is not a picture description.
    /// </summary>
    private const int MaximumContextImageResultLength = MaximumImageSourceLength + 4096;

    /// <summary>
    /// Host-side bound for the Mermaid idle wait. The page bounds itself at 8 seconds;
    /// this one is slightly larger so a page that never reports back cannot stall.
    /// </summary>
    private static readonly TimeSpan MermaidIdleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Host-side bound for the PDF image settle wait. The page bounds itself at
    /// 3 seconds, which is what keeps a dead image host from hanging the export.
    /// </summary>
    private static readonly TimeSpan PdfImageSettleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One preview image selected by the user, whether by clicking it or by opening its
    /// context menu.
    /// </summary>
    private sealed record PreviewContextImageTarget(
        string Source,
        string? AlternativeText,
        bool IsGeneratedDiagram);

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
          const lineAnchorSelector = '[id^="pragma-line-"]';
          let lastAnchor = '';
          let lastAlternativeText = '';
          let lastIndex = -1;

          // The preview DOM is re-rendered for every edit, so element identity does
          // not survive. The nearest pragma-line anchor plus the alternative text
          // identifies the same Markdown source position across those updates, which
          // is what lets a reload pick the image the user is still looking at.
          const anchorOf = image => (image.closest(lineAnchorSelector) || image).id || '';

          const requestOpen = image => {
            const source = image.currentSrc || image.src;
            if (!source) return false;
            const images = [...document.querySelectorAll(imageSelector)];
            lastAnchor = anchorOf(image);
            lastAlternativeText = image.alt || '';
            lastIndex = images.indexOf(image);
            window.chrome.webview.postMessage({
              type: 'open-preview-image',
              source,
              alternativeText: image.alt || '',
              generatedDiagram: image.dataset.wimdGeneratedDiagram === 'true'
            });
            return true;
          };

          // Re-issues the open request for the image that is currently displayed in
          // the independent viewer. Reading the element again is what makes a reload
          // show freshly rendered Mermaid output instead of the data URI captured
          // when the user first clicked the diagram.
          const reopenLastImage = () => {
            if (lastIndex < 0) return false;
            const images = [...document.querySelectorAll(imageSelector)];
            const sameAnchor = lastAnchor
              ? images.filter(image =>
                  anchorOf(image) === lastAnchor && (image.alt || '') === lastAlternativeText)
              : [];
            if (sameAnchor.length === 1) return requestOpen(sameAnchor[0]);

            // A paragraph can hold several images and then share one anchor. Fall
            // back to the recorded position, but only when the alternative text still
            // matches, so a shifted index cannot silently open a different image.
            const atIndex = images[lastIndex];
            return atIndex && (atIndex.alt || '') === lastAlternativeText
              ? requestOpen(atIndex)
              : false;
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

          window.wimdImageOpen = Object.freeze({ reopenLastImage });
        })();
        """;

    private static readonly string CodeBlockCopyScript = $$"""
        (() => {
          const previewSelector = 'main.preview-document';
          const buttonSelector = 'button.wimd-code-copy-button';
          const maximumCodeLength = {{PreviewCodeCopyRequest.MaximumCodeLength}};
          const trustedButtons = new WeakSet();
          const pendingButtons = new Map();
          const resetTimers = new WeakMap();
          let nextRequestId = 1;

          const setReadyState = button => {
            button.disabled = false;
            button.dataset.state = 'ready';
            button.setAttribute('aria-label', '复制代码块');
            button.title = '复制代码';
          };

          const showResult = (button, succeeded) => {
            button.disabled = false;
            button.dataset.state = succeeded ? 'success' : 'failure';
            button.setAttribute('aria-label', succeeded ? '代码已复制' : '复制失败，点击重试');
            button.title = succeeded ? '已复制' : '复制失败，点击重试';
            const previousTimer = resetTimers.get(button);
            if (previousTimer) clearTimeout(previousTimer);
            resetTimers.set(button, setTimeout(() => {
              if (button.isConnected) setReadyState(button);
              resetTimers.delete(button);
            }, 1500));
          };

          const prepareCodeBlocks = () => {
            // A replaced document must not remain rooted by unanswered host requests.
            for (const [id, button] of pendingButtons) {
              if (!button.isConnected) pendingButtons.delete(id);
            }
            const preview = document.querySelector(previewSelector);
            if (!preview) return;
            preview.querySelectorAll('pre').forEach(block => {
              const code = block.querySelector(':scope > code') || block.querySelector('code');
              if (!code
                  || block.classList.contains('mermaid')
                  || code.classList.contains('language-mermaid')
                  || !code.textContent?.trim()
                  || block.parentElement?.classList.contains('wimd-code-block')) return;

              // Keep the button outside the horizontally scrolling pre element so
              // it remains pinned to the visible top-right corner.
              const container = document.createElement('div');
              container.className = 'wimd-code-block';
              block.before(container);
              container.append(block);
              const button = document.createElement('button');
              button.type = 'button';
              button.className = 'wimd-code-copy-button';
              button.dataset.state = 'ready';
              button.setAttribute('aria-label', '复制代码块');
              button.title = '复制代码';
              trustedButtons.add(button);
              container.append(button);
            });
          };

          document.addEventListener('click', event => {
            if (!(event.target instanceof Element) || event.button !== 0) return;
            const button = event.target.closest(buttonSelector);
            if (!button || !trustedButtons.has(button) || button.disabled) return;

            const container = button.closest('.wimd-code-block');
            const code = container?.querySelector(':scope > pre > code') || container?.querySelector('pre code');
            const rawCode = code?.textContent || '';
            const value = rawCode.replace(/\r?\n$/, '');
            if (!value.trim() || value.length > maximumCodeLength) {
              showResult(button, false);
              return;
            }

            event.preventDefault();
            event.stopPropagation();
            const requestId = nextRequestId++;
            pendingButtons.set(requestId, button);
            button.disabled = true;
            button.dataset.state = 'pending';
            button.setAttribute('aria-label', '正在复制代码块');
            button.title = '正在复制';
            window.chrome.webview.postMessage({
              type: 'copy-code-block',
              requestId,
              code: value
            });
          }, true);

          window.wimdCodeCopy = Object.freeze({
            complete(requestId, succeeded) {
              const button = pendingButtons.get(requestId);
              pendingButtons.delete(requestId);
              if (button?.isConnected) showResult(button, Boolean(succeeded));
            }
          });

          document.addEventListener('DOMContentLoaded', prepareCodeBlocks, { once: true });
          document.addEventListener('wimd:preview-updated', prepareCodeBlocks);
          document.addEventListener('wimd:mermaid-rendered', prepareCodeBlocks);
        })();
        """;

    private const string TaskListInteractionScript = """
        (() => {
          const checkboxSelector = 'main.preview-document li.task-list-item input[type="checkbox"]';
          const trustedCheckboxes = new WeakSet();
          const pendingCheckboxes = new Map();
          let nextRequestId = 1;

          const getTaskItem = checkbox => {
            const item = checkbox.closest('li.task-list-item');
            if (!item || item.querySelector('input[type="checkbox"]') !== checkbox) return null;
            return item;
          };

          const getSourceLine = checkbox => {
            const item = getTaskItem(checkbox);
            if (!item || !item.id.startsWith('pragma-line-')) return null;
            const line = Number(item.id.slice(12));
            return Number.isSafeInteger(line) && line >= 0 ? line : null;
          };

          const updateAppearance = checkbox => {
            const item = getTaskItem(checkbox);
            if (!item) return;
            item.classList.toggle('wimd-task-completed', checkbox.checked);
            checkbox.setAttribute(
              'aria-label',
              checkbox.checked ? '标记任务为未完成' : '标记任务为已完成');
            checkbox.title = checkbox.checked ? '点击标记为未完成' : '点击标记为已完成';
          };

          const prepareTasks = () => {
            for (const [id, pending] of pendingCheckboxes) {
              if (!pending.checkbox.isConnected) pendingCheckboxes.delete(id);
            }
            document.querySelectorAll(checkboxSelector).forEach(checkbox => {
              if (getSourceLine(checkbox) === null) return;
              checkbox.disabled = false;
              trustedCheckboxes.add(checkbox);
              updateAppearance(checkbox);
            });
          };

          document.addEventListener('change', event => {
            const checkbox = event.target;
            if (!(checkbox instanceof HTMLInputElement)
                || !checkbox.matches(checkboxSelector)
                || !trustedCheckboxes.has(checkbox)
                || checkbox.disabled) return;

            const sourceLine = getSourceLine(checkbox);
            if (sourceLine === null) return;
            const requestId = nextRequestId++;
            pendingCheckboxes.set(requestId, {
              checkbox,
              previousState: !checkbox.checked
            });
            checkbox.disabled = true;
            updateAppearance(checkbox);
            window.chrome.webview.postMessage({
              type: 'toggle-task-list-item',
              requestId,
              sourceLine,
              completed: checkbox.checked
            });
          }, true);

          window.wimdTaskToggle = Object.freeze({
            complete(requestId, succeeded) {
              const pending = pendingCheckboxes.get(requestId);
              pendingCheckboxes.delete(requestId);
              if (!pending?.checkbox.isConnected) return;
              if (!succeeded) pending.checkbox.checked = pending.previousState;
              pending.checkbox.disabled = false;
              updateAppearance(pending.checkbox);
            }
          });

          document.addEventListener('DOMContentLoaded', prepareTasks, { once: true });
          document.addEventListener('wimd:preview-updated', prepareTasks);
        })();
        """;

    private readonly WebView2 webView;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly IClipboardTextService clipboardTextService;
    private Func<string>? mermaidLibraryLoader;
    private bool mermaidInitialized;
    private readonly PreviewNavigationGate navigationGate = new();
    private readonly PreviewResourceMappingState resourceMappingState = new();
    private TaskCompletionSource<bool> previewReadySource = CreatePreviewReadySource();
    private CoreWebView2? core;
    private PreviewSnapshot? pendingPreview;
    private bool synchronizeCaretWhenReady = true;
    private bool processingPreview;
    private bool navigationInProgress;
    private bool previewPageReady;
    /// <summary>
    /// Entries of the image context menu that is currently on screen. WebView2 only
    /// raises CustomItemSelected for items the host still references, so they must
    /// outlive the request that created them.
    /// </summary>
    private readonly List<CoreWebView2ContextMenuItem> contextMenuItems = [];

    private bool initialized;
    private bool disposed;
    private string? currentPagePolicyIdentity;

    public PreviewWebViewService(
        WebView2 webView,
        IClipboardTextService clipboardTextService,
        string mermaidLibraryScript)
        : this(webView, clipboardTextService, () => mermaidLibraryScript)
    {
    }

    public PreviewWebViewService(
        WebView2 webView,
        IClipboardTextService clipboardTextService,
        Func<string> mermaidLibraryLoader)
    {
        this.webView = webView ?? throw new ArgumentNullException(nameof(webView));
        this.clipboardTextService = clipboardTextService
            ?? throw new ArgumentNullException(nameof(clipboardTextService));
        this.mermaidLibraryLoader = mermaidLibraryLoader ?? throw new ArgumentNullException(nameof(mermaidLibraryLoader));
        this.webView.DefaultBackgroundColor = Color.Transparent;
    }

    public event EventHandler<string>? ExternalNavigationFailed;

    public event EventHandler<string>? PreviewNavigationFailed;

    public event EventHandler<PreviewImageOpenRequestedEventArgs>? PreviewImageOpenRequested;

    public event EventHandler<string>? CodeBlockCopyStatusChanged;

    public event EventHandler<PreviewTaskToggleRequestedEventArgs>? PreviewTaskToggleRequested;

    public event EventHandler<PreviewScrollChangedEventArgs>? ScrollRatioChanged;

    public event EventHandler<PreviewReadyEventArgs>? PreviewReady;

    /// <summary>
    /// Raised when the user picks a save-as format for an image in the live preview.
    /// </summary>
    public event EventHandler<PreviewContextImageExportRequestedEventArgs>? PreviewContextImageExportRequested;

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        if (initialized)
        {
            return;
        }

        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: WebView2UserDataFolder.Resolve())
            .WaitAsync(TimeSpan.FromSeconds(15), lifetimeCancellation.Token).ConfigureAwait(true);
        ThrowIfDisposed();
        await webView.EnsureCoreWebView2Async(environment)
            .WaitAsync(TimeSpan.FromSeconds(15), lifetimeCancellation.Token).ConfigureAwait(true);
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
        await RegisterHostScriptAsync(ScrollReportingScript).ConfigureAwait(true);
        ThrowIfDisposed();
        await RegisterHostScriptAsync(ImageInteractionScript).ConfigureAwait(true);
        ThrowIfDisposed();
        await RegisterHostScriptAsync(CodeBlockCopyScript).ConfigureAwait(true);
        ThrowIfDisposed();
        await RegisterHostScriptAsync(TaskListInteractionScript).ConfigureAwait(true);
        ThrowIfDisposed();
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.AddWebResourceRequestedFilter(
            DocumentImageResourcePattern,
            CoreWebView2WebResourceContext.Image);
        core.WebResourceRequested += OnWebResourceRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        initialized = true;
    }

    /// <summary>
    /// Replaces WebView2's default menu over a preview image with WIMD's own choices.
    ///
    /// The browser menu could only save the generated host document as HTML, so the
    /// right-click path and the viewer's toolbar button disagreed about the format of
    /// the very same picture. Both surfaces now offer the same list. Regions without an
    /// image keep the browser menu, which is what still provides text selection
    /// actions for the rendered document.
    ///
    /// Bug fix: which picture the menu belongs to is resolved from the DOM rather than
    /// from CoreWebView2ContextMenuTarget.SourceUri. Chromium does not report a source
    /// uri for the generated diagrams the Mermaid bridge renders from data urls, so the
    /// previous source-uri check silently rejected exactly those images and left the
    /// browser's single-format menu in place for them. Reading the element under the
    /// pointer also gives the alternative text and the generated-diagram flag, so the
    /// right-click menu and a left click now describe the same picture the same way.
    /// </summary>
    private void OnContextMenuRequested(
        object? sender,
        CoreWebView2ContextMenuRequestedEventArgs eventArgs)
    {
        CoreWebView2? currentCore = core;
        if (currentCore is null
            || eventArgs.ContextMenuTarget is not { Kind: CoreWebView2ContextMenuTargetKind.Image })
        {
            return;
        }

        // Reading the element needs a round trip into the page, so the entries are built
        // under the event deferral and the menu is shown once they are ready.
        _ = BuildImageContextMenuAsync(
            currentCore,
            eventArgs,
            eventArgs.GetDeferral());
    }

    private async Task BuildImageContextMenuAsync(
        CoreWebView2 currentCore,
        CoreWebView2ContextMenuRequestedEventArgs eventArgs,
        CoreWebView2Deferral deferral)
    {
        try
        {
            PreviewContextImageTarget? target = await TryReadImageTargetAsync(eventArgs.Location)
                .ConfigureAwait(true);
            if (target is null)
            {
                // Nothing to act on: leaving MenuItems untouched keeps the browser menu,
                // which is the right answer outside a preview image.
                return;
            }

            PreviewImageSourceDescriptor descriptor = PreviewImageSourceDescriptor.Describe(
                target.Source);
            contextMenuItems.Clear();
            contextMenuItems.Add(CreateImageMenuItem(
                currentCore,
                "在独立窗口中打开",
                () => PreviewImageOpenRequested?.Invoke(
                    this,
                    new PreviewImageOpenRequestedEventArgs(
                        target.Source,
                        target.AlternativeText,
                        target.IsGeneratedDiagram))));

            if (descriptor.Extension.Length > 0)
            {
                foreach (PreviewImageExportFormat format in PreviewImageExportService
                    .GetAvailableFormats(descriptor.Extension))
                {
                    PreviewImageExportFormat selected = format;
                    contextMenuItems.Add(CreateImageMenuItem(
                        currentCore,
                        $"另存为 {PreviewImageExportService.GetDisplayName(descriptor.Extension, descriptor.IsSvg, format)}",
                        () => PreviewContextImageExportRequested?.Invoke(
                            this,
                            new PreviewContextImageExportRequestedEventArgs(
                                target.Source,
                                target.IsGeneratedDiagram,
                                selected))));
                }
            }

            // The list built above replaces the browser menu. Removal and insertion are
            // the operations the WebView2 item collection actually supports.
            eventArgs.MenuItems.Clear();
            foreach (CoreWebView2ContextMenuItem item in contextMenuItems)
            {
                eventArgs.MenuItems.Add(item);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            // A menu that cannot be rebuilt falls back to the browser's own entries
            // instead of failing the interaction.
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Reads the preview image under a point in the page. The coordinates come straight
    /// from the context menu request, so this identifies the element the user actually
    /// right-clicked even when several images share the document.
    /// </summary>
    private async Task<PreviewContextImageTarget?> TryReadImageTargetAsync(Point location)
    {
        if (core is null)
        {
            return null;
        }

        string script = $$"""
            (() => {
              const element = document.elementFromPoint({{location.X}}, {{location.Y}});
              const image = element?.closest('main.preview-document img');
              if (!image) return null;
              return {
                source: image.currentSrc || image.src || '',
                alternativeText: image.alt || '',
                generatedDiagram: image.dataset.wimdGeneratedDiagram === 'true'
              };
            })();
            """;
        string result = await core.ExecuteScriptAsync(script).ConfigureAwait(true);
        if (result.Length is 0 or > MaximumContextImageResultLength)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result);
            return TryReadImageTarget(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the target from a payload that carries a picture's source, alternative
    /// text and generated-diagram flag. Both the click path and the right-click path use
    /// this, so the two can never disagree about which picture is meant.
    /// </summary>
    private static PreviewContextImageTarget? TryReadImageTarget(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("source", out JsonElement sourceElement))
        {
            return null;
        }

        string? source = sourceElement.GetString();
        if (string.IsNullOrWhiteSpace(source) || source.Length > MaximumImageSourceLength)
        {
            return null;
        }

        string? alternativeText = root.TryGetProperty(
            "alternativeText",
            out JsonElement alternativeTextElement)
            ? alternativeTextElement.GetString()
            : null;
        bool isGeneratedDiagram = root.TryGetProperty(
            "generatedDiagram",
            out JsonElement generatedDiagramElement)
            && generatedDiagramElement.ValueKind is JsonValueKind.True;
        return new PreviewContextImageTarget(source, alternativeText, isGeneratedDiagram);
    }

    /// <summary>
    /// Creates one host-owned menu entry. The items are kept for as long as the menu
    /// is on screen, because WebView2 only raises CustomItemSelected for objects the
    /// host still holds.
    /// </summary>
    private static CoreWebView2ContextMenuItem CreateImageMenuItem(
        CoreWebView2 currentCore,
        string label,
        Action onSelected)
    {
        CoreWebView2ContextMenuItem item = currentCore.Environment.CreateContextMenuItem(
            label,
            null,
            CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => onSelected();
        return item;
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
        string pagePolicyIdentity,
        bool synchronizeToCaretWhenReady = true)
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
            pagePolicyIdentity,
            synchronizeToCaretWhenReady);
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

        await WaitForMermaidRenderingAsync().WaitAsync(cancellationToken).ConfigureAwait(true);

        // Remote images and generated Mermaid image surfaces can finish after the
        // DOM update. The page bounds its own wait so a dead image host cannot hang the
        // export, and the outcome is polled for because a script written as an async
        // IIFE returns a pending promise that ExecuteScriptAsync never awaits — which
        // silently skipped this wait and printed PDFs without their images.
        await WebViewScriptResult.RunAsync(
            core,
            """
            (() => {
              window.wimdPdfImagesSettled = null;
              const pending = [...document.images]
                .filter(image => !image.complete)
                .map(image => new Promise(resolve => {
                  image.addEventListener('load', resolve, { once: true });
                  image.addEventListener('error', resolve, { once: true });
                }));
              Promise.race([
                Promise.all(pending),
                new Promise(resolve => setTimeout(resolve, 3000))
              ]).then(() => { window.wimdPdfImagesSettled = true; });
            })();
            """,
            "window.wimdPdfImagesSettled",
            PdfImageSettleTimeout,
            cancellationToken).ConfigureAwait(true);

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

        return previewReadySource.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    /// <summary>
    /// Changes only a host-owned style element in the existing DOM. Theme and font
    /// changes therefore do not navigate WebView2 or reproduce the old white flash.
    /// </summary>
    public Task ApplyAppearanceAsync(string styleSheet)
    {
        ArgumentNullException.ThrowIfNull(styleSheet);
        if (styleSheet.Length > 64 * 1024)
        {
            throw new ArgumentException("预览外观样式长度异常。", nameof(styleSheet));
        }

        string styleLiteral = JsonSerializer.Serialize(styleSheet);
        return ExecuteHostScriptAsync($$"""
            (() => {
              let style = document.getElementById('wimd-host-appearance');
              if (!style) {
                style = document.createElement('style');
                style.id = 'wimd-host-appearance';
                document.head.append(style);
              }
              style.textContent = {{styleLiteral}};
              return true;
            })();
            """);
    }

    /// <summary>
    /// Re-issues the open request for the image the independent viewer is currently
    /// showing, so the viewer reloads whatever the preview now renders at that
    /// position. Reading the element again is what surfaces a re-rendered Mermaid
    /// diagram whose markup changed after the document was edited elsewhere.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a fresh request was posted; <see langword="false"/>
    /// when the preview no longer contains an image at the recorded position.
    /// </returns>
    public async Task<bool> TryReopenLastPreviewImageAsync()
    {
        ThrowIfDisposed();
        if (core is null)
        {
            return false;
        }

        string result = await core.ExecuteScriptAsync(
            "window.wimdImageOpen?.reopenLastImage?.() === true").ConfigureAwait(true);
        return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
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
        PreviewReady?.Invoke(this, new PreviewReadyEventArgs(synchronizeCaretWhenReady));
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
        // Unwind host continuations immediately on close. This cancels our waits,
        // not native WebView2 operations; the control itself is disposed below.
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        previewReadySource.TrySetCanceled();
        pendingPreview = null;
        mermaidLibraryLoader = null;
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
            core.WebResourceRequested -= OnWebResourceRequested;
            core.ContextMenuRequested -= OnContextMenuRequested;
            contextMenuItems.Clear();
            core.RemoveWebResourceRequestedFilter(
                DocumentImageResourcePattern,
                CoreWebView2WebResourceContext.Image);
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
                await EnsureMermaidForBodyAsync(snapshot.BodyHtml).ConfigureAwait(true);
                ThrowIfDisposed();
                synchronizeCaretWhenReady = snapshot.SynchronizeToCaretWhenReady;
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
            .WaitAsync(TimeSpan.FromSeconds(15), lifetimeCancellation.Token)
            .ConfigureAwait(true);
        if (!string.Equals(result, "true", StringComparison.Ordinal))
        {
            return false;
        }

        await WaitForMermaidRenderingAsync().ConfigureAwait(true);
        return true;
    }

    private async Task WaitForMermaidRenderingAsync()
    {
        if (disposed || core is null || !mermaidInitialized)
        {
            return;
        }

        // Host rendering is bounded so malformed or unexpectedly expensive diagrams
        // cannot permanently block typing, view switching, or PDF export.
        //
        // The page publishes the outcome into a slot: as an async IIFE this script used
        // to return a pending promise, so the wait completed instantly and the preview
        // was reported ready — and printed to PDF — before diagrams had finished.
        await WebViewScriptResult.RunAsync(
            core,
            """
            (() => {
              window.wimdMermaidIdle = null;
              if (!window.wimdMermaid) { window.wimdMermaidIdle = true; return; }
              Promise.race([
                window.wimdMermaid.whenIdle(),
                new Promise(resolve => setTimeout(resolve, 8000))
              ]).then(() => { window.wimdMermaidIdle = true; });
            })();
            """,
            "window.wimdMermaidIdle",
            MermaidIdleTimeout,
            lifetimeCancellation.Token).ConfigureAwait(true);
    }

    private async Task EnsureMermaidForBodyAsync(string bodyHtml)
    {
        if (mermaidInitialized || mermaidLibraryLoader is null || core is null
            || (!bodyHtml.Contains("language-mermaid", StringComparison.Ordinal)
                && !bodyHtml.Contains("class=\"mermaid\"", StringComparison.Ordinal)))
        {
            return;
        }

        // Register before any navigation, then also prepare the existing page for
        // its first diagram. CSP remains script-src 'none'; only the host runs this.
        string script = "if (!window.wimdMermaid) {\n"
            + MermaidPreviewScript.Build(mermaidLibraryLoader()) + "\n}";
        await RegisterHostScriptAsync(script).ConfigureAwait(true);
        ThrowIfDisposed();
        if (previewPageReady)
        {
            await core.ExecuteScriptAsync(script)
                .WaitAsync(TimeSpan.FromSeconds(15), lifetimeCancellation.Token).ConfigureAwait(true);
            ThrowIfDisposed();
        }

        mermaidInitialized = true;
        mermaidLibraryLoader = null;
    }

    private async Task RegisterHostScriptAsync(string script)
    {
        ThrowIfDisposed();
        await core!.AddScriptToExecuteOnDocumentCreatedAsync(script)
            .WaitAsync(TimeSpan.FromSeconds(15), lifetimeCancellation.Token).ConfigureAwait(true);
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

    /// <summary>
    /// Bug fix and security boundary: virtual-host mapping would otherwise expose
    /// the original SVG bytes directly. Intercept only SVG image requests and
    /// replace them with a bounded static-profile document; raster files continue
    /// through WebView2's existing read-only folder mapping without extra copies.
    /// </summary>
    private async void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Request.Uri, UriKind.Absolute, out Uri? uri)
            || !uri.IdnHost.Equals(
                LocalImageUrlResolver.VirtualHostName,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(uri.AbsolutePath).Equals(
                ".svg",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CoreWebView2Deferral deferral = eventArgs.GetDeferral();
        try
        {
            string filePath = ResolveLocalSvgPath(
                uri,
                resourceMappingState.DirectoryPath);
            SafeSvgSanitizationResult safeSvg = await SafeSvgSanitizer.SanitizeFileAsync(filePath);
            MemoryStream content = new(safeSvg.Bytes, writable: false);
            eventArgs.Response = core?.Environment.CreateWebResourceResponse(
                content,
                200,
                "OK",
                "Content-Type: image/svg+xml; charset=utf-8\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "X-Content-Type-Options: nosniff");
        }
        catch (Exception exception) when (exception is SafeSvgException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            MemoryStream empty = new(Array.Empty<byte>(), writable: false);
            eventArgs.Response = core?.Environment.CreateWebResourceResponse(
                empty,
                403,
                "SVG Blocked",
                "Content-Type: image/svg+xml; charset=utf-8\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "X-Content-Type-Options: nosniff");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string ResolveLocalSvgPath(Uri uri, string? documentDirectory)
    {
        if (documentDirectory is null)
        {
            throw new InvalidOperationException("当前预览没有本地资源目录。");
        }

        string directory = Path.GetFullPath(documentDirectory);
        string candidate = directory;
        foreach (string encodedSegment in uri.AbsolutePath.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = Uri.UnescapeDataString(encodedSegment);
            if (segment is "." or ".." || segment.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new InvalidOperationException("SVG 地址包含不安全的路径片段。");
            }

            candidate = Path.Combine(candidate, segment);
        }

        string path = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(directory, path);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || !Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new InvalidOperationException("SVG 超出当前文档目录或文件不存在。");
        }

        string current = directory;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("SVG 路径包含符号链接或目录联接。");
            }
        }

        return path;
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
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException
                    or OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException)
                {
                    if (disposed) return;
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
            await WaitForMermaidRenderingAsync().ConfigureAwait(true);
            if (pendingPreview is not null)
            {
                await ProcessPreviewQueueAsync().ConfigureAwait(true);
            }
            else
            {
                SignalPreviewReady();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException
            or OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            if (disposed) return;
            string message = $"预览更新失败：{exception.Message}";
            SignalPreviewFailure(message);
            PreviewNavigationFailed?.Invoke(this, message);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
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
            else if (messageType == "copy-code-block")
            {
                await TryCopyCodeBlockAsync(root).ConfigureAwait(true);
            }
            else if (messageType == "toggle-task-list-item")
            {
                await TryToggleTaskListItemAsync(root).ConfigureAwait(true);
            }
        }
        catch (JsonException)
        {
            // Messages are emitted only by the host-injected script. Invalid data is
            // ignored defensively rather than affecting the editor event loop.
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            if (!disposed)
            {
                PreviewNavigationFailed?.Invoke(this, $"预览交互失败：{exception.Message}");
            }
        }
    }

    /// <summary>
    /// Copies only validated host-script messages. Clipboard access remains in WPF
    /// so preview pages never receive browser clipboard permissions.
    /// </summary>
    private async Task TryCopyCodeBlockAsync(JsonElement root)
    {
        if (!PreviewCodeCopyRequest.TryCreate(root, out PreviewCodeCopyRequest? request)
            || request is null)
        {
            return;
        }

        bool copied = await clipboardTextService.TrySetTextAsync(request.Code).ConfigureAwait(true);
        await CompleteCodeCopyRequestAsync(request.RequestId, copied).ConfigureAwait(true);
        CodeBlockCopyStatusChanged?.Invoke(
            this,
            copied
                ? "已复制代码块"
                : "无法复制代码块：剪贴板正被其他程序占用");
    }

    private async Task CompleteCodeCopyRequestAsync(int requestId, bool succeeded)
    {
        if (disposed || core is null)
        {
            return;
        }

        string successLiteral = succeeded ? "true" : "false";
        await core.ExecuteScriptAsync(
            $"window.wimdCodeCopy?.complete({requestId}, {successLiteral});")
            .ConfigureAwait(true);
    }

    /// <summary>
    /// The host script may request a checkbox change, but only the WPF editor can
    /// approve it after validating the current source line as Markdown task syntax.
    /// This keeps DOM interaction from becoming arbitrary document mutation.
    /// </summary>
    private async Task TryToggleTaskListItemAsync(JsonElement root)
    {
        if (!PreviewTaskToggleRequest.TryCreate(root, out PreviewTaskToggleRequest? request)
            || request is null)
        {
            return;
        }

        bool succeeded = false;
        try
        {
            PreviewTaskToggleRequestedEventArgs eventArgs = new(
                request.SourceLine,
                request.IsCompleted);
            PreviewTaskToggleRequested?.Invoke(this, eventArgs);
            succeeded = eventArgs.Succeeded;
        }
        finally
        {
            // Always release or revert the DOM checkbox, even if a host event
            // handler fails while validating the current editor snapshot.
            await CompleteTaskToggleRequestAsync(request.RequestId, succeeded)
                .ConfigureAwait(true);
        }
    }

    private async Task CompleteTaskToggleRequestAsync(int requestId, bool succeeded)
    {
        if (disposed || core is null)
        {
            return;
        }

        string successLiteral = succeeded ? "true" : "false";
        await core.ExecuteScriptAsync(
            $"window.wimdTaskToggle?.complete({requestId}, {successLiteral});")
            .ConfigureAwait(true);
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
        if (TryReadImageTarget(root) is not { } target)
        {
            return;
        }

        PreviewImageOpenRequested?.Invoke(
            this,
            new PreviewImageOpenRequestedEventArgs(
                target.Source,
                target.AlternativeText,
                target.IsGeneratedDiagram));
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
        string PagePolicyIdentity,
        bool SynchronizeToCaretWhenReady);

    private static bool IsAllowedExternalScheme(string scheme)
    {
        return scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
    }
}
