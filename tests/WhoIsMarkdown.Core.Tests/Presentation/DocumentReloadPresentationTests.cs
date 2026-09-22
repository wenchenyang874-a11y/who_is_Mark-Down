namespace WhoIsMarkdown.Core.Tests.Presentation;

/// <summary>
/// Regression cover for reloading a document that changed outside WIMD. The desktop
/// project is not referenced from the core test assembly, so these assertions read
/// the WPF sources as text and pin the lifetime, prompt and stamp-comparison
/// decisions that a compile-only check cannot catch.
/// </summary>
public sealed class DocumentReloadPresentationTests
{
    [Fact]
    public void 重新加载_作为菜单项和快捷键并可发现()
    {
        string mainWindow = ReadAppSource("MainWindow.xaml");
        string shortcutCatalog = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkDown.App",
            "Shortcuts",
            "ShortcutCatalog.cs"));
        string shortcutsCode = ReadAppSource("MainWindow.Shortcuts.cs");

        Assert.Contains("x:Name=\"ReloadDocumentMenuItem\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Header=\"重新加载(_R)\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"Reload_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"重新加载文档\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Define(\"file.reload\", \"重新加载文档\", Key.F5),",
            shortcutCatalog,
            StringComparison.Ordinal);
        // Defaults, runtime dispatch and the menu hint must all come from the catalog
        // rather than from a second hard-coded key check.
        Assert.Contains("case \"file.reload\":", shortcutsCode, StringComparison.Ordinal);
        Assert.Contains(
            "ReloadDocumentMenuItem.InputGestureText = GetGestureText(\"file.reload\");",
            shortcutsCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 重新加载_存在未保存修改_先经确认再替换编辑器内容()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        int reloadStart = reloadCode.IndexOf(
            "private async Task<bool> ReloadDocumentAsync()",
            StringComparison.Ordinal);
        int readStart = reloadCode.IndexOf(
            "private async Task<bool> ReadDocumentIntoEditorAsync(",
            reloadStart,
            StringComparison.Ordinal);
        Assert.True(reloadStart >= 0 && readStart > reloadStart);
        string reloadMethod = reloadCode[reloadStart..readStart];

        // Reload replaces in-memory text, so it must reuse the ordinary discard/save
        // prompt instead of dropping local edits.
        Assert.Contains("await ConfirmDiscardOrSaveAsync()", reloadMethod, StringComparison.Ordinal);
        int confirmIndex = reloadMethod.IndexOf("ConfirmDiscardOrSaveAsync", StringComparison.Ordinal);
        int readIndex = reloadMethod.IndexOf("ReadDocumentIntoEditorAsync", StringComparison.Ordinal);
        Assert.True(confirmIndex >= 0 && readIndex > confirmIndex);
    }

    [Fact]
    public void 重新加载_文档尚无路径_仍刷新预览而不报错()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        int reloadStart = reloadCode.IndexOf(
            "private async Task<bool> ReloadDocumentAsync()",
            StringComparison.Ordinal);
        int readStart = reloadCode.IndexOf(
            "private async Task<bool> ReadDocumentIntoEditorAsync(",
            reloadStart,
            StringComparison.Ordinal);
        string reloadMethod = reloadCode[reloadStart..readStart];

        Assert.Contains("if (path is null)", reloadMethod, StringComparison.Ordinal);
        Assert.Contains(
            "SchedulePreview(synchronizePreviewToCaretWhenReady: false);",
            reloadMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 外部修改_用目录监视器和文件戳对比识别而不靠定时轮询()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");
        string mainWindowCode = ReadAppSource("MainWindow.xaml.cs");

        Assert.Contains(
            "FileSystemWatcher watcher = new(directory, Path.GetFileName(path))",
            reloadCode,
            StringComparison.Ordinal);
        Assert.Contains("watcher.EnableRaisingEvents = true;", reloadCode, StringComparison.Ordinal);
        // WIMD's own atomic save raises the same events, so detection must compare the
        // saved baseline stamp instead of assuming every event is external.
        Assert.Contains("document.Stamp == current", reloadCode, StringComparison.Ordinal);
        Assert.Contains("reportedExternalStamp == current", reloadCode, StringComparison.Ordinal);
        // The flag closes the race between the write completing and MarkSaved
        // publishing the new baseline stamp.
        Assert.Contains("applyingFileWrite = true;", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("applyingFileWrite = false;", mainWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 外部修改_监视回调切回UI线程且写入后重新校验路径()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        // FileSystemWatcher raises events on a pool thread, so no window or document
        // state may be read before the dispatcher republishes the event.
        Assert.Contains("Dispatcher.BeginInvoke(", reloadCode, StringComparison.Ordinal);
        Assert.Contains(
            "private void QueueExternalDocumentChangeCheck()",
            reloadCode,
            StringComparison.Ordinal);
        // A document switch during the background stamp read must not report a change
        // for the file that is no longer open.
        Assert.Contains(
            "!string.Equals(document.FilePath, path, StringComparison.OrdinalIgnoreCase)",
            reloadCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 外部修改_同一修订只提示一次且默认重新加载()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        Assert.Contains(
            "reportedExternalStamp = current;",
            reloadCode,
            StringComparison.Ordinal);
        int promptStart = reloadCode.IndexOf(
            "private async Task PromptExternalDocumentChangeAsync()",
            StringComparison.Ordinal);
        Assert.True(promptStart >= 0);
        string promptMethod = reloadCode[promptStart..];
        Assert.Contains("MessageBoxButton.YesNo", promptMethod, StringComparison.Ordinal);
        Assert.Contains(
            "if (result != MessageBoxResult.Yes || windowClosed)",
            promptMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 外部修改_监视器在窗口关闭路径停止并释放()
    {
        string commandsCode = ReadAppSource("MainWindow.Commands.cs");
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        Assert.Contains("DetachExternalDocumentWatcher();", commandsCode, StringComparison.Ordinal);
        Assert.Contains("externalDocumentChangeTimer.Stop();", reloadCode, StringComparison.Ordinal);
        Assert.Contains("externalDocumentWatcher.Dispose();", reloadCode, StringComparison.Ordinal);
        // The watcher follows the active path whenever new document content is
        // installed, which is what keeps open, create, restore and reload aligned.
        Assert.Contains("AttachExternalDocumentWatcher();", ReadAppSource("MainWindow.xaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void 刷新图片查看窗口_由预览页重新读取当前图片()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");
        string previewCode = ReadAppSource(Path.Combine("Services", "PreviewWebViewService.cs"));

        // Re-reading the DOM element is what surfaces freshly rendered Mermaid output
        // instead of the data URI captured when the user first clicked the diagram.
        Assert.Contains("window.wimdImageOpen = Object.freeze({ reopenLastImage });", previewCode, StringComparison.Ordinal);
        Assert.Contains("const reopenLastImage = () => {", previewCode, StringComparison.Ordinal);
        Assert.Contains(
            "public async Task<bool> TryReopenLastPreviewImageAsync()",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.wimdImageOpen?.reopenLastImage?.() === true",
            previewCode,
            StringComparison.Ordinal);
        Assert.Contains("await service.TryReopenLastPreviewImageAsync()", reloadCode, StringComparison.Ordinal);
        // The preview queue only signals readiness after the Mermaid bridge finished,
        // so the reopen cannot read a diagram that has not been rendered yet.
        Assert.Contains("await WaitForPreviewRefreshAsync();", reloadCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 刷新图片查看窗口_找不到原图时保留原内容并说明()
    {
        string reloadCode = ReadAppSource("MainWindow.Reload.cs");

        Assert.Contains("viewer.CanLoadAnotherImage", reloadCode, StringComparison.Ordinal);
        Assert.Contains(
            "预览中已找不到刚才打开的图片，图片查看窗口保持原内容",
            reloadCode,
            StringComparison.Ordinal);
    }

    private static string ReadAppSource(string relativePath) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "WhoIsMarkdown.App",
            relativePath));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WhoIsMarkdown.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
