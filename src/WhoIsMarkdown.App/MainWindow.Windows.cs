using System.ComponentModel;
using System.IO;
using System.Windows;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.App.ViewModels;

namespace WhoIsMarkdown.App;

/// <summary>
/// Opens a Markdown file in an independent WIMD process without disturbing the
/// dirty document or workspace state in the current window.
/// </summary>
public partial class MainWindow
{
    private readonly IApplicationWindowLauncher applicationWindowLauncher =
        new ApplicationWindowLauncher();

    private void OpenRecentFileInNewWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetTaggedValue(sender, out string path))
        {
            OpenDocumentInNewWindow(path);
        }
    }

    private void OpenWorkspaceEntryInNewWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item) && item.IsFile)
        {
            OpenDocumentInNewWindow(item.Path);
        }
    }

    private void OpenDocumentInNewWindow(string path)
    {
        try
        {
            applicationWindowLauncher.OpenDocumentInNewWindow(path);
            UpdateStatus("已在新的 WIMD 窗口中打开文件");
        }
        catch (Exception exception) when (exception is ArgumentException
            or FileNotFoundException
            or NotSupportedException
            or PathTooLongException
            or InvalidOperationException
            or Win32Exception)
        {
            UpdateStatus($"无法在新窗口中打开：{exception.Message}");
            MessageBox.Show(
                this,
                $"无法在新的 WIMD 窗口中打开文件：\n{path}\n\n{exception.Message}",
                "无法打开新窗口",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
