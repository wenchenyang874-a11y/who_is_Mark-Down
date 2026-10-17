using System.Windows;
using System.Windows.Threading;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Documents;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Reloads the current document from disk and brings the preview and the independent
/// image viewer back in step with what the file now contains.
///
/// A directory watcher reports edits made by other programs. Without it a preview
/// built from a file that changed outside WIMD is indistinguishable from an
/// up-to-date one, which is exactly the stale-content problem this feature fixes.
/// Automatic reload is opt-in and only accepts an unchanged clean session after
/// reading. Dirty documents always retain the ordinary confirmation protection.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Editors, sync clients and archive tools commonly write a file several times in
    /// a row. Waiting for the burst to settle makes the stamp comparison run once per
    /// real revision instead of once per write.
    /// </summary>
    private const int ExternalChangeSettleMilliseconds = 400;

    private DocumentChangeMonitor? externalDocumentWatcher;
    private DispatcherTimer? externalDocumentChangeTimer;
    private string? watchedDocumentPath;
    private DocumentFileStamp? reportedExternalStamp;
    private bool reportedDocumentUnavailable;
    private bool applyingFileWrite;
    private bool previewImageRefreshRequested;
    private bool externalChangeCheckRunning;
    private bool externalChangeCheckPending;

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
        object identity = document.CaptureSnapshot().DocumentIdentity;
        long openVersion = Volatile.Read(ref documentOpenVersion);
        if (path is not null && !await ConfirmDiscardOrSaveAsync())
        {
            return false;
        }

        // Saving in the confirmation dialog may yield to another open request.
        // That old confirmation never authorizes replacing the newly opened file.
        if (windowClosed || openVersion != Volatile.Read(ref documentOpenVersion)
            || !ReferenceEquals(identity, document.CaptureSnapshot().DocumentIdentity)) return false;

        if (path is not null && !await ReadDocumentIntoEditorAsync(path))
        {
            return false;
        }

        if (path is null)
        {
            SchedulePreview(synchronizePreviewToCaretWhenReady: false, forceBodyReplacement: true);
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
        EditorTextSnapshot expected = document.CaptureSnapshot();
        DocumentFileStamp? expectedStamp = document.Stamp;
        try
        {
            LoadedDocument loaded = await ReadDocumentOffThreadAsync(path);
            if (windowClosed || requestVersion != Volatile.Read(ref documentOpenVersion))
            {
                return false;
            }

            // Confirmation covers only the text present before the read. Do not
            // discard input made while a slow reload was pending, even manually.
            EditorTextSnapshot current = document.CaptureSnapshot();
            if (!ReferenceEquals(expected.DocumentIdentity, current.DocumentIdentity)
                || expected.Revision != current.Revision || document.Stamp != expectedStamp || documentSaveRunning)
            {
                UpdateStatus("重新加载期间文档有新的修改，已保留当前内容；请确认后重试");
                return false;
            }

            document.Load(loaded);
            reportedExternalStamp = null;
            reportedDocumentUnavailable = false;
            ApplyDocumentToEditor(forcePreviewUpdate: true);
            return true;
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            return false;
        }
        catch (DocumentFileException exception)
        {
            if (!windowClosed && requestVersion == Volatile.Read(ref documentOpenVersion))
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
            if (workspaceViewMode == WhoIsMarkdown.Core.Settings.WorkspaceViewMode.EditorOnly)
            {
                await PrepareLatestPreviewForExportAsync(service, forceBodyReplacement: true);
            }
            else
            {
                await WaitForPreviewRefreshAsync();
            }

            if (windowClosed)
            {
                return;
            }
            previewImageRefreshRequested = true;
            if (!await service.TryReopenLastPreviewImageAsync())
            {
                previewImageRefreshRequested = false;
                UpdateStatus("预览中已找不到刚才打开的图片，图片查看窗口保持原内容");
            }
        }
        catch (OperationCanceledException)
        {
            previewImageRefreshRequested = false;
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

        DetachExternalDocumentWatcher(disposeMonitor: false);
        if (path is null || windowClosed)
        {
            return;
        }

        // Registration and handle disposal are worker-owned, including the implicit
        // directory probe made by FileSystemWatcher itself. No UI-thread disk access.
        externalDocumentWatcher ??= new DocumentChangeMonitor();
        externalDocumentWatcher.Changed -= ExternalDocumentWatcher_Changed;
        externalDocumentWatcher.Changed += ExternalDocumentWatcher_Changed;
        watchedDocumentPath = path;
        externalDocumentWatcher.SetPath(path);
    }

    private void DetachExternalDocumentWatcher(bool disposeMonitor = true)
    {
        if (externalDocumentChangeTimer is not null)
        {
            externalDocumentChangeTimer.Stop();
            externalDocumentChangeTimer.Tick -= ExternalDocumentChangeTimer_Tick;
            externalDocumentChangeTimer = null;
        }

        if (externalDocumentWatcher is not null)
        {
            if (disposeMonitor)
            {
                externalDocumentWatcher.Changed -= ExternalDocumentWatcher_Changed;
                externalDocumentWatcher.Dispose();
                externalDocumentWatcher = null;
            }
            else
            {
                externalDocumentWatcher.SetPath(null);
            }
        }

        watchedDocumentPath = null;
        reportedExternalStamp = null;
        reportedDocumentUnavailable = false;
    }

    private void ExternalDocumentWatcher_Changed(object? sender, EventArgs eventArgs)
    {
        // Watcher callbacks run on a pool thread. Only the dispatcher may touch the
        // editor, the preview and the window controls, so the event is republished
        // there before any state is read.
        if (!Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(QueueExternalDocumentChangeCheck));
        }
    }

    private void QueueExternalDocumentChangeCheck()
    {
        if (windowClosed || document.FilePath is null)
        {
            return;
        }
        if (applyingFileWrite)
        {
            externalChangeCheckPending = true;
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
        if (externalChangeCheckRunning)
        {
            externalChangeCheckPending = true;
            return;
        }
        if (windowClosed || document.FilePath is not { } path || applyingFileWrite)
        {
            return;
        }

        // A second watcher burst can arrive during read/prompt/viewer refresh.
        // Serialize the entire workflow and replay only one pending notification.
        externalChangeCheckRunning = true;
        try
        {
            await CheckExternalDocumentChangeAsync(path);
        }
        catch (OperationCanceledException) when (windowClosed) { }
        catch (Exception exception)
        {
            // An async-void watcher entry must never terminate the editor. Keep
            // the user's text; errors report phase/path, never document contents.
            if (!windowClosed) UpdateStatus($"检查外部修改失败（{path}）：{exception.Message}");
        }
        finally
        {
            externalChangeCheckRunning = false;
            if (externalChangeCheckPending)
            {
                externalChangeCheckPending = false;
                QueueExternalDocumentChangeCheck();
            }
        }
    }

    private async Task CheckExternalDocumentChangeAsync(string path)
    {
        long openVersion = Volatile.Read(ref documentOpenVersion);
        DocumentFileStamp current;
        try
        {
            IDocumentFileService service = fileService;
            current = await Task.Run(() => service.Inspect(path))
                .WaitAsync(TimeSpan.FromSeconds(15), windowLifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            return;
        }
        catch (DocumentFileException)
        {
            if (IsCurrentExternalChangeTarget(path, openVersion))
            {
                ReportExternalDocumentLoss();
            }
            return;
        }

        // The document may have been switched or closed while the stamp was read.
        if (!IsCurrentExternalChangeTarget(path, openVersion))
        {
            return;
        }
        if (applyingFileWrite || documentSaveRunning)
        {
            externalChangeCheckPending = true;
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
        reportedDocumentUnavailable = false;
        if (applicationSettings.ExternalFileChanges.Resolve(path) == ExternalFileChangeBehavior.AutoReload
            && !document.IsDirty && !documentSaveRunning)
        {
            await AutoReloadExternalDocumentAsync(path, current, openVersion);
            return;
        }
        await PromptExternalDocumentChangeAsync();
    }

    private bool IsCurrentExternalChangeTarget(string path, long openVersion) =>
        !windowClosed && openVersion == Volatile.Read(ref documentOpenVersion)
        && string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bug fix: auto-reload must not race input, save, another open or a policy
    /// change. Capture pure immutable data, read off-thread, then validate again.
    /// Waiting has a deadline; it does not claim to abort a blocked driver read.
    /// </summary>
    private async Task AutoReloadExternalDocumentAsync(string path, DocumentFileStamp detectedStamp, long openVersion)
    {
        EditorTextSnapshot expected = document.CaptureSnapshot();
        DocumentFileStamp? baseline = document.Stamp;
        LoadedDocument loaded;
        try
        {
            loaded = await ReadDocumentOffThreadAsync(path)
                .WaitAsync(TimeSpan.FromSeconds(15), windowLifetimeCancellation.Token);
        }
        catch (DocumentFileException exception)
        {
            if (IsCurrentExternalChangeTarget(path, openVersion))
                UpdateStatus($"自动更新文档失败，已保留当前内容（{path}）：{exception.Message}");
            return;
        }
        catch (TimeoutException)
        {
            if (IsCurrentExternalChangeTarget(path, openVersion))
                UpdateStatus($"自动更新等待超过 15 秒，已保留当前内容（{path}）；底层读取可能仍在执行，可按 F5 重试");
            return;
        }

        if (!IsCurrentExternalChangeTarget(path, openVersion)) return;
        if (applyingFileWrite || documentSaveRunning || document.Stamp != baseline || loaded.Stamp != detectedStamp)
        {
            // A newer disk revision or our own save wins. Reinspect after settling
            // rather than installing data captured before that operation completed.
            reportedExternalStamp = null;
            externalChangeCheckPending = true;
            return;
        }
        if (applicationSettings.ExternalFileChanges.Resolve(path) != ExternalFileChangeBehavior.AutoReload
            || !document.TryAutoReload(loaded, expected, baseline))
        {
            await PromptExternalDocumentChangeAsync();
            return;
        }

        reportedExternalStamp = null;
        reportedDocumentUnavailable = false;
        ApplyDocumentToEditor(forcePreviewUpdate: true, preserveEditorPosition: true);
        UpdateStatus("文档已自动更新为外部最新内容");
        await RefreshOpenPreviewImageViewAsync();
    }

    /// <summary>
    /// Reports an external edit once per file revision and offers to reload. Reload
    /// still runs the ordinary unsaved-changes prompt afterwards, so local edits are
    /// never dropped without an explicit decision.
    /// </summary>
    private async Task PromptExternalDocumentChangeAsync()
    {
        string? path = document.FilePath;
        long openVersion = Volatile.Read(ref documentOpenVersion);
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

        if (path is null || !IsCurrentExternalChangeTarget(path, openVersion)) return;

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
