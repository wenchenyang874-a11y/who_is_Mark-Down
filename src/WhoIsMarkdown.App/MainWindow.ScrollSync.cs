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
    private readonly DispatcherTimer editorScrollSyncTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(35),
    };

    private bool applyingPreviewScrollToEditor;
    private bool applyingEditorScrollToPreview;

    private void InitializeScrollSynchronization()
    {
        editorScrollSyncTimer.Tick += EditorScrollSyncTimer_Tick;
        Editor.TextArea.TextView.ScrollOffsetChanged += EditorTextView_ScrollOffsetChanged;
    }

    private void EditorTextView_ScrollOffsetChanged(object? sender, EventArgs eventArgs)
    {
        if (applyingPreviewScrollToEditor || workspaceViewMode is not ViewModels.WorkspaceViewMode.EditorAndPreview)
        {
            return;
        }

        editorScrollSyncTimer.Stop();
        editorScrollSyncTimer.Start();
    }

    private async void EditorScrollSyncTimer_Tick(object? sender, EventArgs eventArgs)
    {
        editorScrollSyncTimer.Stop();
        if (previewService is null || applyingPreviewScrollToEditor)
        {
            return;
        }

        double maximum = Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight);
        double ratio = maximum <= 0 ? 0 : Math.Clamp(Editor.VerticalOffset / maximum, 0, 1);
        applyingEditorScrollToPreview = true;
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
        if (applyingEditorScrollToPreview || workspaceViewMode is not ViewModels.WorkspaceViewMode.EditorAndPreview)
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

    private async Task SynchronizePreviewToCaretAsync()
    {
        if (previewService is null || workspaceViewMode is ViewModels.WorkspaceViewMode.EditorOnly)
        {
            return;
        }

        try
        {
            await previewService.ScrollToSourceLineAsync(Editor.TextArea.Caret.Line - 1);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            UpdateStatus($"预览定位失败：{exception.Message}");
        }
    }

    private void PreviewService_PreviewReady(object? sender, EventArgs eventArgs)
    {
        _ = SynchronizePreviewToCaretAsync();
    }

    private void DisposeScrollSynchronization()
    {
        editorScrollSyncTimer.Stop();
        editorScrollSyncTimer.Tick -= EditorScrollSyncTimer_Tick;
        Editor.TextArea.TextView.ScrollOffsetChanged -= EditorTextView_ScrollOffsetChanged;
    }
}
