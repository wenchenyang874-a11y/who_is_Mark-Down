using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhoIsMarkdown.App.Shortcuts;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Captures user shortcut assignments. Changes are staged locally and become
/// persistent only after every gesture has passed reserved-key and collision checks.
/// </summary>
public partial class ShortcutSettingsWindow : Window
{
    private readonly ObservableCollection<ShortcutEditorItemViewModel> items = [];
    private ShortcutEditorItemViewModel? capturingItem;

    public ShortcutSettingsWindow(IReadOnlyDictionary<string, ShortcutGesture> assignments)
    {
        InitializeComponent();
        foreach (ShortcutCommandDefinition definition in ShortcutCatalog.Definitions)
        {
            ShortcutGesture gesture = assignments.TryGetValue(definition.Id, out ShortcutGesture? current)
                ? current
                : definition.DefaultGesture;
            items.Add(new ShortcutEditorItemViewModel(definition, gesture));
        }

        DataContext = items;
    }

    public IReadOnlyDictionary<string, ShortcutGesture> Assignments =>
        items.ToDictionary(item => item.CommandId, item => item.Gesture, StringComparer.Ordinal);

    private void StartCapture_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: ShortcutEditorItemViewModel item })
        {
            return;
        }

        StopCapture();
        capturingItem = item;
        item.IsCapturing = true;
        ValidationMessage.Foreground = (Brush)FindResource("TextSecondaryBrush");
        ValidationMessage.Text = "现在按下新的组合键；按 Esc 取消本次设置。";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (capturingItem is null)
        {
            return;
        }

        eventArgs.Handled = true;
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        if (key == Key.Escape)
        {
            StopCapture();
            ValidationMessage.Text = string.Empty;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            ValidationMessage.Foreground = Brushes.Firebrick;
            ValidationMessage.Text = "Windows 键组合由系统保留，请选择其他组合。";
            return;
        }

        CaptureGesture(ShortcutCatalog.FromInput(key, modifiers));
    }

    private void CaptureGesture(ShortcutGesture candidate)
    {
        ShortcutEditorItemViewModel? target = capturingItem;
        if (target is null)
        {
            return;
        }

        if (!ShortcutCatalog.TryValidateGesture(candidate, out string validationError))
        {
            ValidationMessage.Foreground = Brushes.Firebrick;
            ValidationMessage.Text = validationError;
            return;
        }

        ShortcutEditorItemViewModel? conflict = items.FirstOrDefault(
            item => !ReferenceEquals(item, target)
                && ShortcutCatalog.GetIdentity(item.Gesture) == ShortcutCatalog.GetIdentity(candidate));
        if (conflict is not null)
        {
            ValidationMessage.Foreground = Brushes.Firebrick;
            ValidationMessage.Text = $"{ShortcutCatalog.FormatGesture(candidate)} 已用于“{conflict.DisplayName}”。";
            return;
        }

        target.Gesture = candidate;
        ValidationMessage.Foreground = Brushes.SeaGreen;
        ValidationMessage.Text = $"已设置“{target.DisplayName}”：{ShortcutCatalog.FormatGesture(candidate)}";
        StopCapture();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs eventArgs)
    {
        StopCapture();
        foreach (ShortcutEditorItemViewModel item in items)
        {
            item.Gesture = item.Definition.DefaultGesture;
        }

        ValidationMessage.Foreground = System.Windows.Media.Brushes.SeaGreen;
        ValidationMessage.Text = "已恢复默认快捷键；点击“保存”后生效。";
    }

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        StopCapture();
        IReadOnlyList<IReadOnlyList<string>> conflicts =
            ShortcutConflictDetector.FindConflicts(Assignments);
        if (conflicts.Count > 0)
        {
            ValidationMessage.Foreground = Brushes.Firebrick;
            ValidationMessage.Text = "存在重复快捷键，请修正后再保存。";
            return;
        }

        DialogResult = true;
    }

    private void StopCapture()
    {
        if (capturingItem is not null)
        {
            capturingItem.IsCapturing = false;
            capturingItem = null;
        }
    }
}
