using System.IO;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Documents;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.App;

/// <summary>
/// Hosts the editor shell and coordinates dialogs and document lifecycle events.
/// Persistence, Markdown conversion, settings, and WebView security remain in
/// focused services so the shell can evolve without absorbing core behavior.
/// </summary>
public partial class MainWindow : Window
{
    private const int PreviewDebounceMilliseconds = 100;

    private readonly IDocumentFileService fileService = new DocumentFileService();
    private readonly IMarkdownRenderer markdownRenderer = new MarkdownRenderer();
    private readonly IPreviewDocumentBuilder previewDocumentBuilder = new PreviewDocumentBuilder();
    private readonly DocumentEditorViewModel document = new();

    private PreviewWebViewService? previewService;
    private CancellationTokenSource? previewCancellation;
    private string previewStyleSheet = string.Empty;
    private int untitledCounter = 1;
    private long previewVersion;
    private long documentOpenVersion;
    private bool applyingDocumentText;
    private bool closeApproved;
    private bool closeWorkflowRunning;
    private bool windowClosed;

    public MainWindow()
    {
        InitializeComponent();
        InitializeAppearanceController();
        InitializeScrollSynchronization();
        DataContext = document;
        document.StartNew(untitledCounter);
        Editor.TextArea.Caret.PositionChanged += EditorCaret_PositionChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        LoadApplicationSettings();
        string? startupWorkspacePath = GetStartupWorkspacePath();
        if (startupWorkspacePath is not null)
        {
            await OpenWorkspaceAsync(startupWorkspacePath);
        }

        string? startupDocumentPath = GetStartupDocumentPath();
        if (startupDocumentPath is not null)
        {
            await OpenDocumentAsync(startupDocumentPath);
        }

        try
        {
            previewStyleSheet = ReadPreviewStyleSheet();
            previewService = new PreviewWebViewService(Preview, clipboardTextService);
            previewService.ExternalNavigationFailed += PreviewService_ExternalNavigationFailed;
            previewService.PreviewNavigationFailed += PreviewService_PreviewNavigationFailed;
            previewService.PreviewImageOpenRequested += PreviewService_PreviewImageOpenRequested;
            previewService.CodeBlockCopyStatusChanged += PreviewService_CodeBlockCopyStatusChanged;
            previewService.PreviewTaskToggleRequested += PreviewService_TaskToggleRequested;
            previewService.ScrollRatioChanged += PreviewService_ScrollRatioChanged;
            previewService.PreviewReady += PreviewService_PreviewReady;
            await previewService.InitializeAsync();

            if (!windowClosed)
            {
                SchedulePreview();
                UpdateStatus("准备就绪");
                Editor.Focus();
            }
        }
        catch (ObjectDisposedException) when (windowClosed)
        {
            // Closing during WebView initialization is an expected lifecycle race.
        }
        catch (Exception exception)
        {
            UpdateStatus($"预览初始化失败：{exception.Message}");
        }
    }

    private async void New_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (workspaceRootPath is not null)
        {
            string parentDirectory = GetWorkspaceParentDirectory(sender) ?? workspaceRootPath;
            await CreateWorkspaceFileAsync(parentDirectory);
            return;
        }

        if (!await ConfirmDiscardOrSaveAsync())
        {
            return;
        }

        document.StartNew(++untitledCounter);
        ApplyDocumentToEditor();
        UpdateStatus("已新建文档");
    }

    private async void Open_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!await ConfirmDiscardOrSaveAsync())
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Title = "打开 Markdown 文档",
            Filter = "Markdown 文档 (*.md;*.markdown)|*.md;*.markdown|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenDocumentAsync(dialog.FileName);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        await SaveCurrentDocumentAsync(forceSaveAs: false);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs eventArgs)
    {
        await SaveCurrentDocumentAsync(forceSaveAs: true);
    }

    private void Exit_Click(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void Editor_TextChanged(object sender, EventArgs eventArgs)
    {
        if (applyingDocumentText)
        {
            return;
        }

        document.Text = Editor.Text;
        SchedulePreview();
        UpdateStatus();
    }

    private void EditorCaret_PositionChanged(object? sender, EventArgs eventArgs)
    {
        CaretText.Text = $"行 {Editor.TextArea.Caret.Line}，列 {Editor.TextArea.Caret.Column}";
        _ = SynchronizePreviewToCaretAsync();
    }

    private void Preview_PreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        // WebView2 owns its native scroll handling. Scroll events are reported by a
        // host-injected script; this handler intentionally does not mark the event.
    }

    private async Task OpenDocumentAsync(string path)
    {
        // Workspace double-clicks may start overlapping asynchronous reads. Only the
        // latest request may update the editor; otherwise an older, slower read can
        // finish last and make switching appear to jump back to the previous file.
        long requestVersion = Interlocked.Increment(ref documentOpenVersion);
        try
        {
            LoadedDocument loadedDocument = await fileService.ReadAsync(path);
            if (requestVersion != Volatile.Read(ref documentOpenVersion))
            {
                return;
            }

            document.Load(loadedDocument);
            ApplyDocumentToEditor();
            RecordRecentFile(loadedDocument.Path);
            UpdateStatus("文档已打开");
        }
        catch (DocumentFileException exception)
        {
            if (requestVersion == Volatile.Read(ref documentOpenVersion))
            {
                ShowFileError("无法打开文档", exception);
            }
        }
    }

    private async Task<bool> SaveCurrentDocumentAsync(bool forceSaveAs)
    {
        string? targetPath = forceSaveAs || document.FilePath is null
            ? SelectSavePath()
            : document.FilePath;

        if (targetPath is null)
        {
            return false;
        }

        try
        {
            DocumentFileStamp stamp = await fileService.WriteAsync(document.CreateWriteRequest(targetPath));
            document.MarkSaved(targetPath, stamp);
            RecordRecentFile(targetPath);
            SchedulePreview();
            UpdateStatus("文档已保存");
            return true;
        }
        catch (DocumentFileException exception)
        {
            ShowFileError("无法保存文档", exception);
            return false;
        }
    }

    private async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (!document.IsDirty)
        {
            return true;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"“{document.DisplayName}”包含尚未保存的修改。是否保存？",
            "保存修改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result switch
        {
            MessageBoxResult.Yes => await SaveCurrentDocumentAsync(forceSaveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private string? SelectSavePath()
    {
        SaveFileDialog dialog = new()
        {
            Title = "保存 Markdown 文档",
            Filter = "Markdown 文档 (*.md)|*.md|Markdown 文档 (*.markdown)|*.markdown",
            DefaultExt = ".md",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = document.DisplayName,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void ApplyDocumentToEditor()
    {
        applyingDocumentText = true;
        try
        {
            Editor.Document = new TextDocument(document.Text);
            Editor.Document.UndoStack.ClearAll();
            Editor.CaretOffset = 0;
        }
        finally
        {
            applyingDocumentText = false;
        }

        SchedulePreview();
        UpdateStatus();
        Editor.Focus();
    }

    private void SchedulePreview()
    {
        if (previewService is null || windowClosed)
        {
            return;
        }

        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        long version = ++previewVersion;
        RemoteImagePolicy remoteImagePolicy = CreateRemoteImagePolicy();
        _ = RefreshPreviewAsync(
            document.Text,
            document.FilePath,
            remoteImagePolicy,
            version,
            previewCancellation.Token);
    }

    /// <summary>
    /// Bug fix: preview rendering now carries the document path so relative images
    /// can be mapped safely, while stale asynchronous renders remain cancellable.
    /// </summary>
    private async Task RefreshPreviewAsync(
        string markdown,
        string? documentPath,
        RemoteImagePolicy remoteImagePolicy,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PreviewDebounceMilliseconds, cancellationToken);
            string body = await Task.Run(
                () => markdownRenderer.RenderBody(markdown, documentPath, remoteImagePolicy),
                cancellationToken);
            string visibleBody = previewDocumentBuilder.GetVisibleBody(body);
            string page = previewDocumentBuilder.Build(body, previewStyleSheet, remoteImagePolicy);

            PreviewWebViewService? service = previewService;
            if (!cancellationToken.IsCancellationRequested
                && version == previewVersion
                && service is not null)
            {
                // Replacing content can clamp WebView's scroll range. Suppress that
                // host-generated report so an edit never scrolls AvalonEdit.
                SuppressPreviewScrollEcho();
                await service.ShowAsync(
                    page,
                    visibleBody,
                    documentPath,
                    remoteImagePolicy.Identity);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer editor snapshot superseded this render request.
        }
        catch (Exception exception)
        {
            UpdateStatus($"预览失败：{exception.Message}");
        }
    }

    private void CancelPreviewWork()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
    }

    private static string ReadPreviewStyleSheet()
    {
        // Bug fix: resolve the resource from WIMD's component assembly rather
        // than the process entry assembly. This keeps preview initialization
        // working when MainWindow is hosted by UI smoke-test executables.
        string assemblyName = typeof(MainWindow).Assembly.GetName().Name
            ?? throw new InvalidOperationException("无法确定预览资源程序集。");
        Uri resourceUri = new(
            $"/{assemblyName};component/Resources/preview.css",
            UriKind.Relative);
        StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
        if (resource is null)
        {
            throw new InvalidOperationException("找不到预览样式资源。");
        }

        using StreamReader reader = new(resource.Stream);
        return reader.ReadToEnd();
    }

    private void PreviewService_ExternalNavigationFailed(object? sender, string message)
    {
        UpdateStatus(message);
    }

    private void PreviewService_PreviewNavigationFailed(object? sender, string message)
    {
        UpdateStatus(message);
    }

    private void PreviewService_CodeBlockCopyStatusChanged(object? sender, string message)
    {
        UpdateStatus(message);
    }

    private void About_Click(object sender, RoutedEventArgs eventArgs)
    {
        System.Version? version = typeof(MainWindow).Assembly.GetName().Version;
        string displayVersion = version is null ? "未知版本" : version.ToString(3);
        MessageBox.Show(
            this,
            $"WIMD v{displayVersion}\n\n本地、离线优先的 Markdown 实时预览编辑器。",
            "关于 WIMD",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string? GetStartupWorkspacePath()
    {
        // Directory arguments are treated strictly as paths. Supporting them here
        // also prepares the Windows shell integration without evaluating commands.
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(Directory.Exists);
    }

    private static string? GetStartupDocumentPath()
    {
        // Windows shell commands quote paths before passing them to WIMD. The CLR
        // performs argument splitting, so the application treats each value only as
        // a path and never evaluates it as a command.
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(path =>
                File.Exists(path)
                && (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase)));
    }

    private void ShowFileError(string title, DocumentFileException exception)
    {
        UpdateStatus(exception.Message);
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void UpdateStatus(string? message = null)
    {
        if (message is not null)
        {
            StatusText.Text = message;
        }

        PathText.Text = document.FilePath ?? "未保存";
        CaretText.Text = $"行 {Editor.TextArea.Caret.Line}，列 {Editor.TextArea.Caret.Column}";
    }
}
