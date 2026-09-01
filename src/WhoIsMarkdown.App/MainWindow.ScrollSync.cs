using System.Windows.Threading;
using WhoIsMarkdown.App.Services;

namespace WhoIsMarkdown.App;

/// <summary>
/// Synchronizes editor and preview position. Caret moves use Markdig's source-line
/// anchors for semantic alignment; free scrolling uses normalized scroll ranges so
/// documents with images, headings, and code blocks remain proportionally aligned.
/// </summary>
public partial class MainWindow
{
    private const int ProgrammaticPreviewScrollSuppressionMilliseconds = 250;

    private readonly DispatcherTimer editorScrollSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(35),
    };

    private bool applyingPreviewScrollToEditor;
    private bool applyingEditorScrollToPreview;
    private long suppressPreviewScrollUntilTick;

    private void InitializeScrollSynchronization()
    {
        editorScrollSyncTimer.Tick += EditorScrollSyncTimer_Tick;
        Editor.TextArea.TextView.ScrollOffsetChanged += EditorTextView_ScrollOffsetChanged;
    }

    private void EditorTextView_ScrollOffsetChanged(object? sender, EventArgs eventArgs)
    {
        if (applyingPreviewScrollToEditor
            || suppressEditorDrivenPreviewSyncUntilReady
            || workspaceViewMode is not ViewModels.WorkspaceViewMode.EditorAndPreview)
        {
            return;
        }

        editorScrollSyncTimer.Stop();
        editorScrollSyncTimer.Start();
    }

    private async void EditorScrollSyncTimer_Tick(object? sender, EventArgs eventArgs)
    {
        editorScrollSyncTimer.Stop();
        if (previewService is null
            || applyingPreviewScrollToEditor
            || suppressEditorDrivenPreviewSyncUntilReady)
        {
            return;
        }

        double maximum = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        double ratio = maximum <= 0 ? 0 : Math.Clamp(Editor.VerticalOffset / maximum, 0, 1);
        applyingEditorScrollToPreview = true;
        SuppressPreviewScrollEcho();
        try
        {
            await previewService.ScrollToRatioAsync(ratio);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            UpdateStatus($"预览滚动同步失败：{exception.Message}");
        }
        finally
        {
            applyingEditorScrollToPreview = false;
        }
    }

    private void PreviewService_ScrollRatioChanged(
        object? sender,
        PreviewScrollChangedEventArgs eventArgs)
    {
        if (applyingEditorScrollToPreview
            || IsPreviewScrollEchoSuppressed()
            || workspaceViewMode is not ViewModels.WorkspaceViewMode.EditorAndPreview)
        {
            return;
        }

        double maximum = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        applyingPreviewScrollToEditor = true;
        try
        {
            Editor.ScrollToVerticalOffset(maximum * eventArgs.Ratio);
        }
        finally
        {
            applyingPreviewScrollToEditor = false;
        }
    }

    /// <summary>
    /// Bug fix: moving the caret scrolls only the preview, and only when its source
    /// anchor is outside the preview's 25%-75% comfort zone. WebView2 reports
    /// host-requested scrolling too, so the echo guard prevents that report from
    /// moving AvalonEdit after a simple mouse click.
    /// </summary>
    private async Task SynchronizePreviewToCaretAsync()
    {
        if (previewService is null || workspaceViewMode is ViewModels.WorkspaceViewMode.EditorOnly)
        {
            return;
        }

        SuppressPreviewScrollEcho();
        try
        {
            await previewService.EnsureSourceLineInComfortZoneAsync(Editor.TextArea.Caret.Line - 1);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            UpdateStatus($"预览定位失败：{exception.Message}");
        }
    }

    private void PreviewService_PreviewReady(object? sender, PreviewReadyEventArgs eventArgs)
    {
        if (!eventArgs.SynchronizeToCaret)
        {
            // AvalonEdit can emit a delayed scroll-offset change after replacing
            // the one task marker. Keep that notification from overwriting the
            // preview scroll position restored by the DOM update.
            ReleaseTaskPreviewPositionSuppression();
            return;
        }

        _ = SynchronizePreviewToCaretAsync();
    }

    private void ReleaseTaskPreviewPositionSuppression()
    {
        editorScrollSyncTimer.Stop();
        suppressEditorDrivenPreviewSyncUntilReady = false;
    }

    private void SuppressPreviewScrollEcho()
    {
        suppressPreviewScrollUntilTick = Environment.TickCount64
            + ProgrammaticPreviewScrollSuppressionMilliseconds;
    }

    private bool IsPreviewScrollEchoSuppressed() =>
        Environment.TickCount64 <= suppressPreviewScrollUntilTick;

    private void DisposeScrollSynchronization()
    {
        editorScrollSyncTimer.Stop();
        editorScrollSyncTimer.Tick -= EditorScrollSyncTimer_Tick;
        Editor.TextArea.TextView.ScrollOffsetChanged -= EditorTextView_ScrollOffsetChanged;
    }
}
