using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;
using WhoIsMarkdown.App.ViewModels;

namespace WhoIsMarkdown.App;

/// <summary>
/// Registers application-level shortcuts and owns terminal window cleanup.
/// Routed commands keep working while the editor has keyboard focus.
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
    private static readonly RoutedUICommand NewDocumentShortcutCommand = CreateCommand(
        "新建文档",
        nameof(NewDocumentShortcutCommand),
        Key.N,
        ModifierKeys.Control);

    private static readonly RoutedUICommand OpenDocumentShortcutCommand = CreateCommand(
        "打开文档",
        nameof(OpenDocumentShortcutCommand),
        Key.O,
        ModifierKeys.Control);

    private static readonly RoutedUICommand SaveDocumentShortcutCommand = CreateCommand(
        "保存文档",
        nameof(SaveDocumentShortcutCommand),
        Key.S,
        ModifierKeys.Control);

    private static readonly RoutedUICommand SaveDocumentAsShortcutCommand = CreateCommand(
        "另存为",
        nameof(SaveDocumentAsShortcutCommand),
        Key.S,
        ModifierKeys.Control | ModifierKeys.Shift);

    private static readonly RoutedUICommand PreviewOnlyShortcutCommand = CreateCommand(
        "仅预览",
        nameof(PreviewOnlyShortcutCommand),
        Key.D1,
        ModifierKeys.Control);

    private static readonly RoutedUICommand SplitViewShortcutCommand = CreateCommand(
        "编辑和预览",
        nameof(SplitViewShortcutCommand),
        Key.D2,
        ModifierKeys.Control);

    private static readonly RoutedUICommand EditorOnlyShortcutCommand = CreateCommand(
        "仅编辑",
        nameof(EditorOnlyShortcutCommand),
        Key.D3,
        ModifierKeys.Control);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        CommandBindings.Add(new CommandBinding(
            NewDocumentShortcutCommand,
            (_, _) => New_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(
            OpenDocumentShortcutCommand,
            (_, _) => Open_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(
            SaveDocumentShortcutCommand,
            (_, _) => Save_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(
            SaveDocumentAsShortcutCommand,
            (_, _) => SaveAs_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(
            PreviewOnlyShortcutCommand,
            (_, _) => SetWorkspaceViewMode(WorkspaceViewMode.PreviewOnly)));
        CommandBindings.Add(new CommandBinding(
            SplitViewShortcutCommand,
            (_, _) => SetWorkspaceViewMode(WorkspaceViewMode.EditorAndPreview)));
        CommandBindings.Add(new CommandBinding(
            EditorOnlyShortcutCommand,
            (_, _) => SetWorkspaceViewMode(WorkspaceViewMode.EditorOnly)));
    }

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
        DisposeAppearanceController();

        if (previewService is not null)
        {
            previewService.ExternalNavigationFailed -= PreviewService_ExternalNavigationFailed;
            previewService.PreviewNavigationFailed -= PreviewService_PreviewNavigationFailed;
            previewService.Dispose();
            previewService = null;
        }

        Editor.TextArea.Caret.PositionChanged -= EditorCaret_PositionChanged;
        base.OnClosed(e);
    }

    private static RoutedUICommand CreateCommand(
        string text,
        string name,
        Key key,
        ModifierKeys modifiers)
    {
        InputGestureCollection gestures = [new KeyGesture(key, modifiers)];
        return new RoutedUICommand(text, name, typeof(MainWindow), gestures);
    }
}
