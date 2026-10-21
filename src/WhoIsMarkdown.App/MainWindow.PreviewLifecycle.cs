using System.Windows.Threading;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Bug fix: editor-only windows used to start Chromium and render every keystroke.
/// Initialization is shared by visible preview and explicit export, never by hidden
/// editor updates. A hidden, unchanged DOM is kept intact to preserve F9 scroll state.
/// </summary>
public partial class MainWindow
{
    private Task? previewInitialization;
    private bool previewInitialized;
    private bool windowStartupComplete;
    private bool previewNeedsRefresh = true;
    private bool previewRenderPending;

    private async Task EnsureVisiblePreviewAsync()
    {
        if (!windowStartupComplete || windowClosed || workspaceViewMode == WorkspaceViewMode.EditorOnly)
        {
            return;
        }

        try
        {
            await EnsurePreviewInitializedAsync();
            if (!windowClosed && workspaceViewMode != WorkspaceViewMode.EditorOnly && previewNeedsRefresh)
            {
                SchedulePreview(synchronizePreviewToCaretWhenReady: false);
            }
        }
        catch (Exception exception)
        {
            if (!windowClosed)
            {
                UpdateStatus($"预览初始化失败：{exception.Message}");
            }
        }
    }

    private Task EnsurePreviewInitializedAsync()
    {
        ObjectDisposedException.ThrowIf(windowClosed, this);
        // Let the WPF shell render before native startup. The task is installed
        // immediately, so file-open prefetch, visible preview and export coalesce.
        return previewInitialization ??= Dispatcher.InvokeAsync(
            InitializePreviewAsync, DispatcherPriority.Background, windowLifetimeCancellation.Token).Task.Unwrap();
    }

    private async Task InitializePreviewAsync()
    {
        ObjectDisposedException.ThrowIf(windowClosed, this);
        // Keep CPU preparation off the dispatcher and overlap it with native
        // browser startup. This path is already deferred until shell rendering
        // and never requested by editor-only startup or ordinary hidden edits.
        Task rendererPreparation = PreparePreviewRendererAsync();
        await Task.WhenAll(rendererPreparation, InitializePreviewBrowserAsync());
        ObjectDisposedException.ThrowIf(windowClosed, this);
        previewInitialized = true;
    }

    private async Task PreparePreviewRendererAsync()
    {
        // Moving construction earlier must not hide its nested timings behind
        // the normal fast-render suppression threshold. Keep this first-use
        // scope detailed even when Chromium is still initializing concurrently.
        bool detailed = CoreDiagnosticBridge.Detailed.Value;
        CoreDiagnosticBridge.Detailed.Value = true;
        try
        {
            await DiagnosticLog.MeasureAsync(DiagnosticStage.PreviewRendererPrepare,
                () => markdownRenderer.PrepareAsync(windowLifetimeCancellation.Token)
                    .WaitAsync(TimeSpan.FromSeconds(15), windowLifetimeCancellation.Token), diagnosticWindowId);
        }
        finally
        {
            CoreDiagnosticBridge.Detailed.Value = detailed;
        }
    }

    private async Task InitializePreviewBrowserAsync()
    {
        previewStyleSheet = DiagnosticLog.Measure(DiagnosticStage.PreviewResources,
            () => ReadComponentTextResource("preview.css", "找不到预览样式资源。"), diagnosticWindowId);
        // Ordinary documents do not pay the multi-megabyte Mermaid load/parse cost.
        PreviewWebViewService service = new(Preview, clipboardTextService,
            () => ReadComponentTextResource("mermaid.min.js", "找不到 Mermaid 离线渲染资源。"));
        service.DiagnosticWindowId = diagnosticWindowId;
        previewService = service;
        service.ExternalNavigationFailed += PreviewService_ExternalNavigationFailed;
        service.PreviewNavigationFailed += PreviewService_PreviewNavigationFailed;
        service.PreviewImageOpenRequested += PreviewService_PreviewImageOpenRequested;
        service.PreviewContextImageExportRequested += PreviewService_PreviewContextImageExportRequested;
        service.CodeBlockCopyStatusChanged += PreviewService_CodeBlockCopyStatusChanged;
        service.PreviewTaskToggleRequested += PreviewService_TaskToggleRequested;
        service.ScrollRatioChanged += PreviewService_ScrollRatioChanged;
        service.PreviewReady += PreviewService_PreviewReady;
        service.PreviewContentApplying += PreviewService_PreviewContentApplying;
        await DiagnosticLog.MeasureAsync(DiagnosticStage.PreviewInitialize, service.InitializeAsync, diagnosticWindowId);
    }

    private void PreparePreviewForFileOpen()
    {
        WorkspaceViewMode mode = !windowStartupComplete && restoredWindowState is not null
            && Enum.TryParse(restoredWindowState.ViewMode, out WorkspaceViewMode restoredMode)
            && Enum.IsDefined(restoredMode)
                ? restoredMode
                : applicationSettings.ResolveFileOpenViewMode();
        if (windowClosed || mode == WorkspaceViewMode.EditorOnly) return;

        _ = ObservePreviewPreparationAsync(EnsurePreviewInitializedAsync());
    }

    private static async Task ObservePreviewPreparationAsync(Task preparation)
    {
        try
        {
            await preparation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Prefetch never owns document status. The original task is awaited
            // and reported by the visible-preview/export path when it is needed.
            // Observe failures even if the file read fails or its window closes.
        }
    }

    private void UpdatePreviewVisibility()
    {
        if (workspaceViewMode == WorkspaceViewMode.EditorOnly)
        {
            // Only cancelled work needs another render. A simple hide/show must
            // never replace the existing DOM or restart the Mermaid pipeline.
            previewNeedsRefresh |= previewRenderPending;
            CancelPreviewWork();
        }
        else
        {
            _ = EnsureVisiblePreviewAsync();
        }
    }
}
