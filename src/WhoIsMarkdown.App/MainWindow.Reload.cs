using System.IO;
using System.Windows;
using System.Windows.Threading;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.App;

/// <summary>
/// Reloads the current document from disk and brings the preview and the independent
/// image viewer back in step with what the file now contains.
///
/// A directory watcher reports edits made by other programs. Without it a preview
/// built from a file that changed outside WIMD is indistinguishable from an
/// up-to-date one, which is exactly the stale-content problem this feature fixes.
/// Detection only ever reports; nothing is reloaded until the user agrees, so an
/// external write can never discard unsaved local edits on its own.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Editors, sync clients and archive tools commonly write a file several times in
    /// a row. Waiting for the burst to settle makes the stamp comparison run once per
    /// real revision instead of once per write.
    /// </summary>
    private const int ExternalChangeSettleMilliseconds = 400;

    private FileSystemWatcher? externalDocumentWatcher;
    private DispatcherTimer? externalDocumentChangeTimer;
    private string? watchedDocumentPath;
    private DocumentFileStamp? reportedExternalStamp;
    private bool reportedDocumentUnavailable;
    private bool applyingFileWrite;
    private bool previewImageRefreshRequested;

    private async void Reload_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ReloadDocumentAsync();
    }

    /// <summary>
    /// Re-reads the document from disk and then refreshes what is derived from it.
    /// A document without a path keeps its text and only refreshes the preview, so
    /// the menu item and F5 stay useful before the first save.
    /// </summary>
    private async Task<bool> ReloadDocumentAsync()
    {
        string? path = document.FilePath;
        if (path is not null && !await ConfirmDiscardOrSaveAsync())
        {
            return false;
        }

        if (path is not null && !await ReadDocumentIntoEditorAsync(path))
        {
            return false;
        }

        if (path is null)
        {
            SchedulePreview(synchronizePreviewToCaretWhenReady: false);
        }

        UpdateStatus("已重新加载文档");
        await RefreshOpenPreviewImageViewAsync();
        return true;
    }

    /// <summary>
    /// Replaces the editor content with the file on disk. The shared open version
    /// keeps a reload and a document switch from overwriting each other when their
    /// reads finish out of order.
    /// </summary>
    private async Task<bool> ReadDocumentIntoEditorAsync(string path)
    {
        long requestVersion = Interlocked.Increment(ref documentOpenVersion);
        try
        {
            LoadedDocument loaded = await Task.Run(() => fileService.ReadAsync(path));
            if (requestVersion != Volatile.Read(ref documentOpenVersion))
            {
                return false;
            }

            document.Load(loaded);
            reportedExternalStamp = null;
            reportedDocumentUnavailable = false;
            ApplyDocumentToEditor();
            return true;
        }
        catch (DocumentFileException exception)
        {
            if (requestVersion == Volatile.Read(ref documentOpenVersion))
            {
                ShowFileError("无法重新加载文档", exception);
            }

            return false;
        }
    }

    /// <summary>
    /// Reloads the image the independent viewer is showing. The preview page re-reads
    /// the element it last opened, so the viewer picks up re-rendered Mermaid output
    /// as well as a local or remote image whose bytes changed on disk.
    /// </summary>
    private async Task RefreshOpenPreviewImageViewAsync()
    {
        if (previewImageWindow is not { } viewer || !viewer.CanLoadAnotherImage)
        {
            return;
        }

        PreviewWebViewService? service = previewService;
        if (service is null)
        {
            return;
        }

        try
        {
            // The reopen request travels through the same open-image path as a user
            // click, so the handler needs to know it should report a refresh.
            await WaitForPreviewRefreshAsync();
            previewImageRefreshRequested = true;
            if (!await service.TryReopenLastPreviewImageAsync())
            {
                previewImageRefreshRequested = false;
                UpdateStatus("预览中已找不到刚才打开的图片，图片查看窗口保持原内容");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            previewImageRefreshRequested = false;
            UpdateStatus($"刷新图片查看窗口失败：{exception.Message}");
        }
    }

    /// <summary>
    /// Waits for the debounced render the reload just queued. The preview queue
    /// signals readiness only after the Mermaid bridge has finished, so a generated
    /// diagram is already re-rendered when the viewer reads it back.
    /// </summary>
    private async Task WaitForPreviewRefreshAsync()
    {
        if (previewService is not { } service || windowClosed)
        {
            return;
        }

        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPreviewReady(object? sender, PreviewReadyEventArgs eventArgs) =>
            ready.TrySetResult(true);

        service.PreviewReady += OnPreviewReady;
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // A stalled render must not block the reload. The viewer simply keeps the
            // content it already had.
        }
        finally
        {
            service.PreviewReady -= OnPreviewReady;
        }
    }

    /// <summary>
    /// Watches the directory that holds the current document. Called from the single
    /// place that installs new document content, so opening, creating, restoring and
    /// reloading a document all keep the watcher aligned with the active path.
    /// </summary>
    private void AttachExternalDocumentWatcher()
    {
        string? path = document.FilePath;
        if (string.Equals(watchedDocumentPath, path, StringComparison.OrdinalIgnoreCase)
            && externalDocumentWatcher is not null)
        {
            return;
        }

        DetachExternalDocumentWatcher();
        if (path is null || windowClosed)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime
                    | NotifyFilters.FileName,
            };
            watcher.Changed += ExternalDocumentWatcher_Changed;
            watcher.Created += ExternalDocumentWatcher_Changed;
            watcher.Deleted += ExternalDocumentWatcher_Changed;
            watcher.Renamed += ExternalDocumentWatcher_Changed;
            watcher.EnableRaisingEvents = true;
            externalDocumentWatcher = watcher;
            watchedDocumentPath = path;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // Watching is a convenience layered on top of editing. A path that cannot
            // be watched — a disconnected share, for example — must never block
            // opening, saving or closing the document itself.
            externalDocumentWatcher = null;
            watchedDocumentPath = null;
        }
    }

    private void DetachExternalDocumentWatcher()
    {
        if (externalDocumentChangeTimer is not null)
        {
            externalDocumentChangeTimer.Stop();
            externalDocumentChangeTimer.Tick -= ExternalDocumentChangeTimer_Tick;
            externalDocumentChangeTimer = null;
        }

        if (externalDocumentWatcher is not null)
        {
            externalDocumentWatcher.EnableRaisingEvents = false;
            externalDocumentWatcher.Changed -= ExternalDocumentWatcher_Changed;
            externalDocumentWatcher.Created -= ExternalDocumentWatcher_Changed;
            externalDocumentWatcher.Deleted -= ExternalDocumentWatcher_Changed;
            externalDocumentWatcher.Renamed -= ExternalDocumentWatcher_Changed;
            externalDocumentWatcher.Dispose();
            externalDocumentWatcher = null;
        }

        watchedDocumentPath = null;
        reportedExternalStamp = null;
        reportedDocumentUnavailable = false;
    }

    private void ExternalDocumentWatcher_Changed(object sender, FileSystemEventArgs eventArgs)
    {
        // Watcher callbacks run on a pool thread. Only the dispatcher may touch the
        // editor, the preview and the window controls, so the event is republished
        // there before any state is read.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(QueueExternalDocumentChangeCheck));
    }

    private void QueueExternalDocumentChangeCheck()
    {
        if (windowClosed || document.FilePath is null || applyingFileWrite)
        {
            return;
        }

        if (externalDocumentChangeTimer is null)
        {
            externalDocumentChangeTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ExternalChangeSettleMilliseconds),
            };
            externalDocumentChangeTimer.Tick += ExternalDocumentChangeTimer_Tick;
        }

        // Every event in a burst restarts the interval, so only the last one is
        // inspected and one revision cannot prompt the user more than once.
        externalDocumentChangeTimer.Stop();
        externalDocumentChangeTimer.Start();
    }

    private async void ExternalDocumentChangeTimer_Tick(object? sender, EventArgs eventArgs)
    {
        externalDocumentChangeTimer?.Stop();
        if (windowClosed || document.FilePath is not { } path || applyingFileWrite)
        {
            return;
        }

        DocumentFileStamp current;
        try
        {
            current = await Task.Run(() => fileService.Inspect(path));
        }
        catch (DocumentFileException)
        {
            ReportExternalDocumentLoss();
            return;
        }

        // The document may have been switched or closed while the stamp was read.
        if (windowClosed || !string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // WIMD's own atomic save raises the same watcher events. Comparing the stamp
        // against the saved baseline tells an external write apart from our own
        // without having to guess from timing.
        if (document.Stamp == current || reportedExternalStamp == current)
        {
            return;
        }

        reportedExternalStamp = current;
        await PromptExternalDocumentChangeAsync();
    }

    /// <summary>
    /// Reports an external edit once per file revision and offers to reload. Reload
    /// still runs the ordinary unsaved-changes prompt afterwards, so local edits are
    /// never dropped without an explicit decision.
    /// </summary>
    private async Task PromptExternalDocumentChangeAsync()
    {
        UpdateStatus("当前文档已被其他程序修改，按 F5 或使用“文件 - 重新加载”获取最新内容");
        MessageBoxResult result = MessageBox.Show(
            this,
            $"“{document.DisplayName}”已被其他程序修改。\n\n是否重新加载并显示最新内容？",
            "文档已在外部被修改",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (result != MessageBoxResult.Yes || windowClosed)
        {
            return;
        }

        await ReloadDocumentAsync();
    }

    private void ReportExternalDocumentLoss()
    {
        if (reportedDocumentUnavailable || windowClosed)
        {
            return;
        }

        reportedDocumentUnavailable = true;
        UpdateStatus("当前文档已被删除或移动，按 F5 可尝试重新加载");
    }
}
