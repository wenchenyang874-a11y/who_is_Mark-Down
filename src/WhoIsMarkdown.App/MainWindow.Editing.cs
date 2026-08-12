using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.App;

/// <summary>
/// Owns explicit AvalonEdit operations and Markdown formatting actions. WPF's
/// generic editing command routing does not query AvalonEdit reliably from menu
/// items, so commands call the editor API and compute enabled state directly.
/// </summary>
public partial class MainWindow
{
    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs eventArgs)
    {
        bool hasSelection = Editor.SelectionLength > 0;
        UndoMenuItem.IsEnabled = Editor.CanUndo;
        RedoMenuItem.IsEnabled = Editor.CanRedo;
        CutMenuItem.IsEnabled = hasSelection;
        CopyMenuItem.IsEnabled = hasSelection;
        PasteMenuItem.IsEnabled = Clipboard.ContainsText();
    }

    private void Undo_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor.CanUndo)
        {
            Editor.Undo();
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor.CanRedo)
        {
            Editor.Redo();
        }
    }

    private void Cut_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor.SelectionLength > 0)
        {
            Editor.Cut();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor.SelectionLength > 0)
        {
            Editor.Copy();
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Clipboard.ContainsText())
        {
            Editor.Paste();
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs eventArgs)
    {
        Editor.SelectAll();
        Editor.Focus();
    }

    private void Heading_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string level })
        {
            ApplyMarkdownFormat($"h{level}");
        }
    }

    private void MarkdownFormat_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: string format })
        {
            ApplyMarkdownFormat(format);
        }
    }

    private void ApplyMarkdownFormat(string format)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(
            Editor.Text,
            Editor.SelectionStart,
            Editor.SelectionLength,
            format);

        Editor.Document.BeginUpdate();
        try
        {
            Editor.Document.Text = edit.Text;
            Editor.Select(edit.SelectionStart, edit.SelectionLength);
            Editor.CaretOffset = edit.CaretOffset;
        }
        finally
        {
            Editor.Document.EndUpdate();
        }

        Editor.Focus();
    }
}
