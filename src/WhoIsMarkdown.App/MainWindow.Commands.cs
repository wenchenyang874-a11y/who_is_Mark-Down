using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace WhoIsMarkdown.App;

/// <summary>
/// Owns the terminal window cleanup and releases application-level resources.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the window lifetime; OnClosed disposes all owned resources.")]
[SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible for improved performance",
    Justification = "Core service interfaces are deliberate extension and test seams at the desktop composition boundary.")]
public partial class MainWindow
{
    /// <summary>
    /// Bug fix: the previous Closing handler cancelled every close request, even for
    /// clean documents, and immediately re-entered Close. Clean windows now close in
    /// one pass when preferences are drained. Pending preferences are drained
    /// asynchronously before the unchanged installer/dirty-document decisions.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        bool skipSettingsFlush = settingsCloseFlushApproved;
        settingsCloseFlushApproved = false;
        if (!closeApproved && !skipSettingsFlush
            && (settingsWriter?.NeedsFlush == true || settingsWriter is null && settingsChangedBeforeLoad))
        {
            e.Cancel = true;
            if (closeWorkflowRunning) return;
            closeWorkflowRunning = true;
            try
            {
                if (await FlushSettingsBeforeCloseAsync())
                {
                    // Re-enter the existing installer/dirty-document rules only
                    // after draining preferences. Capture recovery text then, not
                    // before an asynchronous wait in which the user can still edit.
                    settingsCloseFlushApproved = true;
                    _ = Dispatcher.InvokeAsync(new Action(Close));
                }
            }
            catch (Exception exception)
            {
                // async-void close entry must never terminate other windows.
                UpdateStatus($"关闭前保存设置失败（{exception.GetType().Name}），请稍后重试。");
            }
            finally { closeWorkflowRunning = false; }
            return;
        }

        // Installer-triggered shutdown uses a one-time local snapshot, including
        // dirty text, so it must not show the ordinary discard/save prompt.
        if (TryPersistUpdateRestartWindowState())
        {
            closeApproved = true;
            CancelPreviewWork();
            return;
        }

        if (closeApproved || !document.IsDirty)
        {
            closeApproved = true;
            CancelPreviewWork();
            return;
        }

        e.Cancel = true;
        if (closeWorkflowRunning)
        {
            return;
        }

        closeWorkflowRunning = true;
        try
        {
            if (await ConfirmDiscardOrSaveAsync())
            {
                closeApproved = true;
                await Dispatcher.InvokeAsync(new Action(Close));
            }
        }
        finally
        {
            closeWorkflowRunning = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        windowClosed = true;
        CancelWorkspaceReads();
        DisposeSettingsPersistence();
        windowLifetimeCancellation.Cancel();
        windowLifetimeCancellation.Dispose();
        Interlocked.Increment(ref documentOpenVersion);
        CancelPreviewWork();
        markdownRenderer.Dispose();
        CancelImageWork();
        CancelPreviewImageWork();
        DisposeAppearanceController();
        DisposePerformanceMonitor();
        // Stops the settle timer and releases the directory handle. A watcher that
        // outlived its window would keep posting to a dispatcher that no longer has
        // a document to compare against.
        DetachExternalDocumentWatcher();
        DisposeUpdateController();
        DisposeWindowChrome();
        DisposeScrollSynchronization();

        if (previewService is not null)
        {
            previewService.ExternalNavigationFailed -= PreviewService_ExternalNavigationFailed;
            previewService.PreviewNavigationFailed -= PreviewService_PreviewNavigationFailed;
            previewService.PreviewImageOpenRequested -= PreviewService_PreviewImageOpenRequested;
            previewService.PreviewContextImageExportRequested -= PreviewService_PreviewContextImageExportRequested;
            previewService.CodeBlockCopyStatusChanged -= PreviewService_CodeBlockCopyStatusChanged;
            previewService.PreviewTaskToggleRequested -= PreviewService_TaskToggleRequested;
            previewService.ScrollRatioChanged -= PreviewService_ScrollRatioChanged;
            previewService.PreviewReady -= PreviewService_PreviewReady;
            previewService.PreviewContentApplying -= PreviewService_PreviewContentApplying;
            previewService.Dispose();
            previewService = null;
        }
        else
        {
            Preview.Dispose();
        }

        Editor.TextArea.Caret.PositionChanged -= EditorCaret_PositionChanged;
        base.OnClosed(e);
    }
}
