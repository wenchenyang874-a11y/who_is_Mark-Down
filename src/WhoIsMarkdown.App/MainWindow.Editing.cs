using System.Runtime.InteropServices;
using System.Windows;
using WhoIsMarkdown.App.Services;
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
        PasteMenuItem.IsEnabled = ClipboardContainsText() || ClipboardImageReader.ContainsImage();
    }

    /// <summary>
    /// Bug fix: another process can briefly lock the Windows clipboard. Opening the
    /// Edit menu must remain usable in that case, so Paste is disabled for this menu
    /// opening instead of allowing the clipboard COM exception to abort the popup.
    /// </summary>
    private static bool ClipboardContainsText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (ExternalException)
        {
            return false;
        }
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

    private async void Paste_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ClipboardImageReader.ContainsImage())
        {
            await PasteClipboardImageAsync();
            return;
        }

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
        if (sender is FrameworkElement { Tag: string level })
        {
            ApplyMarkdownFormat($"h{level}");
        }
    }

    private void MarkdownFormat_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: string format })
        {
            if (format == "table")
            {
                ShowTableSizeDialog();
                return;
            }

            ApplyMarkdownFormat(format);
        }
    }

    private void ShowTableSizeDialog()
    {
        TableSizeDialog dialog = new()
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            Editor.Focus();
            return;
        }

        MarkdownTextEdit edit = MarkdownFormattingService.ApplyTable(
            Editor.Text,
            Editor.SelectionStart,
            Editor.SelectionLength,
            dialog.SelectedRowCount,
            dialog.SelectedColumnCount);
        ApplyMarkdownTextEdit(edit);
    }

    private void ApplyMarkdownFormat(string format)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(
            Editor.Text,
            Editor.SelectionStart,
            Editor.SelectionLength,
            format);

        ApplyMarkdownTextEdit(edit);
    }

    private void ApplyMarkdownTextEdit(MarkdownTextEdit edit)
    {
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

    /// <summary>
    /// A preview checkbox is only a request to modify a source-line marker. The
    /// current editor text is parsed again here so a stale DOM cannot write to an
    /// arbitrary line that is no longer valid Markdown task syntax.
    /// </summary>
    private void PreviewService_TaskToggleRequested(
        object? sender,
        PreviewTaskToggleRequestedEventArgs eventArgs)
    {
        if (!MarkdownTaskListService.TryCreateStateEdit(
                Editor.Text,
                eventArgs.SourceLine,
                eventArgs.IsCompleted,
                out MarkdownTaskStateEdit? edit)
            || edit is null)
        {
            UpdateStatus("任务状态未更新：源文本已发生变化");
            return;
        }

        if (edit.HasChanged)
        {
            // Bug fix: the preview checkbox is the user's active scroll surface.
            // Carry that intent through the debounced DOM refresh so PreviewReady
            // does not immediately relocate the page to an unrelated editor caret.
            applyingPreviewTaskEdit = true;
            suppressEditorDrivenPreviewSyncUntilReady = true;
            editorScrollSyncTimer.Stop();
            try
            {
                Editor.Document.Replace(edit.Offset, 1, edit.Replacement.ToString());
            }
            finally
            {
                applyingPreviewTaskEdit = false;
            }
        }

        eventArgs.Succeeded = true;
        UpdateStatus(eventArgs.IsCompleted ? "任务已标记为完成" : "任务已标记为未完成");
    }
}
