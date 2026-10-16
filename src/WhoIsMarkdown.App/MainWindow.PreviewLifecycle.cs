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
        previewStyleSheet = ReadComponentTextResource("preview.css", "找不到预览样式资源。");
        // Ordinary documents do not pay the multi-megabyte Mermaid load/parse cost.
        PreviewWebViewService service = new(Preview, clipboardTextService,
            () => ReadComponentTextResource("mermaid.min.js", "找不到 Mermaid 离线渲染资源。"));
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
        await service.InitializeAsync();
        ObjectDisposedException.ThrowIf(windowClosed, this);
        previewInitialized = true;
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
