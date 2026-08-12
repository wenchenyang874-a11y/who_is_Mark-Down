using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Input;

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

    private static readonly RoutedUICommand BoldShortcutCommand = CreateCommand(
        "粗体",
        nameof(BoldShortcutCommand),
        Key.B,
        ModifierKeys.Control);

    private static readonly RoutedUICommand ItalicShortcutCommand = CreateCommand(
        "斜体",
        nameof(ItalicShortcutCommand),
        Key.I,
        ModifierKeys.Control);

    private static readonly RoutedUICommand CycleViewShortcutCommand = CreateCommand(
        "循环切换视图",
        nameof(CycleViewShortcutCommand),
        Key.F9,
        ModifierKeys.None);

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (eventArgs.Key == Key.F9 && modifiers == ModifierKeys.None)
        {
            CycleWorkspaceViewMode();
            eventArgs.Handled = true;
            return;
        }

        if (modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (eventArgs.Key is >= Key.D1 and <= Key.D6)
        {
            int level = eventArgs.Key - Key.D0;
            ApplyMarkdownFormat($"h{level}");
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.B)
        {
            ApplyMarkdownFormat("bold");
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.I)
        {
            ApplyMarkdownFormat("italic");
            eventArgs.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        CommandBindings.Add(new CommandBinding(NewDocumentShortcutCommand, (_, _) => New_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(OpenDocumentShortcutCommand, (_, _) => Open_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(SaveDocumentShortcutCommand, (_, _) => Save_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(SaveDocumentAsShortcutCommand, (_, _) => SaveAs_Click(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(CycleViewShortcutCommand, (_, _) => CycleWorkspaceViewMode()));
        CommandBindings.Add(new CommandBinding(BoldShortcutCommand, (_, _) => ApplyMarkdownFormat("bold")));
        CommandBindings.Add(new CommandBinding(ItalicShortcutCommand, (_, _) => ApplyMarkdownFormat("italic")));

        for (int level = 1; level <= 6; level++)
        {
            int capturedLevel = level;
            RoutedUICommand headingCommand = CreateCommand(
                $"{level} 级标题",
                $"Heading{level}ShortcutCommand",
                Key.D0 + level,
                ModifierKeys.Control);
            CommandBindings.Add(new CommandBinding(headingCommand, (_, _) => ApplyMarkdownFormat($"h{capturedLevel}")));
        }
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
        DisposeScrollSynchronization();

        if (previewService is not null)
        {
            previewService.ExternalNavigationFailed -= PreviewService_ExternalNavigationFailed;
            previewService.PreviewNavigationFailed -= PreviewService_PreviewNavigationFailed;
            previewService.ScrollRatioChanged -= PreviewService_ScrollRatioChanged;
            previewService.PreviewReady -= PreviewService_PreviewReady;
            previewService.Dispose();
            previewService = null;
        }

        Editor.TextArea.Caret.PositionChanged -= EditorCaret_PositionChanged;
        base.OnClosed(e);
    }

    private static RoutedUICommand CreateCommand(string text, string name, Key key, ModifierKeys modifiers)
    {
        InputGestureCollection gestures = [new KeyGesture(key, modifiers)];
        return new RoutedUICommand(text, name, typeof(MainWindow), gestures);
    }
}
