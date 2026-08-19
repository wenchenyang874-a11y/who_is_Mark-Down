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
    /// one pass; only dirty documents use the asynchronous confirmation workflow.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
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
        CancelPreviewWork();
        CancelImageWork();
        CancelPreviewImageWork();
        DisposeAppearanceController();
        DisposeScrollSynchronization();

        if (previewService is not null)
        {
            previewService.ExternalNavigationFailed -= PreviewService_ExternalNavigationFailed;
            previewService.PreviewNavigationFailed -= PreviewService_PreviewNavigationFailed;
            previewService.PreviewImageOpenRequested -= PreviewService_PreviewImageOpenRequested;
            previewService.ScrollRatioChanged -= PreviewService_ScrollRatioChanged;
            previewService.PreviewReady -= PreviewService_PreviewReady;
            previewService.Dispose();
            previewService = null;
        }

        Editor.TextArea.Caret.PositionChanged -= EditorCaret_PositionChanged;
        base.OnClosed(e);
    }
}
