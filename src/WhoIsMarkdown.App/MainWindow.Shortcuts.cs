using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WhoIsMarkdown.App.Shortcuts;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Resolves persisted shortcut overrides, dispatches keyboard input, and keeps UI
/// hints synchronized with the exact runtime assignments.
/// </summary>
public partial class MainWindow
{
    private IReadOnlyDictionary<string, ShortcutGesture> shortcutAssignments =
        ShortcutCatalog.CreateDefaultAssignments();

    private IReadOnlyDictionary<string, string> commandIdsByGesture =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void ApplyShortcutSettings()
    {
        shortcutAssignments = ShortcutCatalog.ResolveAssignments(applicationSettings.ShortcutOverrides);
        commandIdsByGesture = shortcutAssignments.ToDictionary(
            item => ShortcutCatalog.GetIdentity(item.Value),
            item => item.Key,
            StringComparer.OrdinalIgnoreCase);
        UpdateShortcutHints();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Windows)
            || key is Key.None or Key.System
                or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin)
        {
            return;
        }

        ShortcutGesture gesture = ShortcutCatalog.FromInput(key, modifiers);
        if (!commandIdsByGesture.TryGetValue(ShortcutCatalog.GetIdentity(gesture), out string? commandId))
        {
            return;
        }

        ExecuteShortcut(commandId);
        eventArgs.Handled = true;
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs eventArgs)
    {
        ShortcutSettingsWindow dialog = new(shortcutAssignments) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        applicationSettings = applicationSettings with
        {
            ShortcutOverrides = ShortcutCatalog.CreateOverrides(dialog.Assignments),
        };
        bool settingsSaved = TrySaveApplicationSettings();
        ApplyShortcutSettings();
        if (settingsSaved)
        {
            UpdateStatus("快捷键设置已保存");
        }
    }

    private void ExecuteShortcut(string commandId)
    {
        switch (commandId)
        {
            case "file.new":
                New_Click(this, new RoutedEventArgs());
                break;
            case "file.open":
                Open_Click(this, new RoutedEventArgs());
                break;
            case "file.save":
                Save_Click(this, new RoutedEventArgs());
                break;
            case "file.save-as":
                SaveAs_Click(this, new RoutedEventArgs());
                break;
            case "view.cycle":
                CycleWorkspaceViewMode();
                break;
            case "heading.1":
            case "heading.2":
            case "heading.3":
            case "heading.4":
            case "heading.5":
            case "heading.6":
                ApplyMarkdownFormat($"h{commandId[^1]}");
                break;
            case "format.bold":
                ApplyMarkdownFormat("bold");
                break;
            case "format.italic":
                ApplyMarkdownFormat("italic");
                break;
            case "format.strike":
                ApplyMarkdownFormat("strike");
                break;
            case "format.inline-code":
                ApplyMarkdownFormat("inline-code");
                break;
            case "format.code-block":
                ApplyMarkdownFormat("code-block");
                break;
            case "format.link":
                ApplyMarkdownFormat("link");
                break;
            case "format.image":
                ApplyMarkdownFormat("image");
                break;
            case "format.ordered-list":
                ApplyMarkdownFormat("ordered-list");
                break;
            case "format.unordered-list":
                ApplyMarkdownFormat("unordered-list");
                break;
            case "format.quote":
                ApplyMarkdownFormat("quote");
                break;
            case "format.task-list":
                ApplyMarkdownFormat("task-list");
                break;
        }
    }

    private void UpdateShortcutHints()
    {
        NewDocumentMenuItem.InputGestureText = GetGestureText("file.new");
        OpenDocumentMenuItem.InputGestureText = GetGestureText("file.open");
        SaveDocumentMenuItem.InputGestureText = GetGestureText("file.save");
        SaveAsDocumentMenuItem.InputGestureText = GetGestureText("file.save-as");
        CycleViewModeMenuItem.InputGestureText = GetGestureText("view.cycle");

        foreach (MenuItem menuItem in MarkdownFormatMenuItem.Items.OfType<MenuItem>())
        {
            if (menuItem.Tag is string format
                && shortcutAssignments.ContainsKey($"format.{format}"))
            {
                menuItem.InputGestureText = GetGestureText($"format.{format}");
            }
        }

        foreach (Button button in MarkdownToolbar.Children.OfType<Button>())
        {
            if (button.Tag is not string tag)
            {
                continue;
            }

            string commandId = int.TryParse(tag, out int level)
                ? $"heading.{level}"
                : $"format.{tag}";
            if (!shortcutAssignments.ContainsKey(commandId))
            {
                continue;
            }

            string toggleHint = tag is "bold" or "italic" or "strike"
                ? "，可再次执行取消"
                : string.Empty;
            ShortcutCommandDefinition definition = ShortcutCatalog.Definitions
                .Single(item => item.Id == commandId);
            button.ToolTip = $"{definition.DisplayName} ({GetGestureText(commandId)}{toggleHint})";
        }
    }

    private string GetGestureText(string commandId) =>
        ShortcutCatalog.FormatGesture(shortcutAssignments[commandId]);
}
