using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Workspace;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates folder-workspace UI and delegates all disk mutations to the
/// workspace-scoped core service. Destructive operations require an explicit user
/// confirmation and never reuse the non-destructive recent-file removal command.
/// </summary>
public partial class MainWindow
{
    private readonly IWorkspaceFileService workspaceFileService = new WorkspaceFileService();
    private string? workspaceRootPath;
    private WorkspaceTreeItemViewModel? selectedWorkspaceItem;
    private bool workspaceOperationRunning;

    public ObservableCollection<WorkspaceTreeItemViewModel> WorkspaceItems { get; } = [];

    private async void OpenFolder_Click(object sender, RoutedEventArgs eventArgs)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "打开 WIMD 工作区文件夹",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenWorkspaceAsync(dialog.FolderName);
        }
    }

    private async Task OpenWorkspaceAsync(string path)
    {
        if (workspaceOperationRunning)
        {
            return;
        }

        workspaceOperationRunning = true;
        try
        {
            string root = await Task.Run(() => workspaceFileService.Open(path));
            workspaceRootPath = root;
            WorkspaceFolderNameText.Text = Path.GetFileName(root);
            WorkspaceFolderPathText.Text = root;
            WorkspacePaneContent.Visibility = Visibility.Visible;
            RecentPaneContent.Visibility = Visibility.Collapsed;
            CloseWorkspaceMenuItem.IsEnabled = true;
            SetRecentPaneExpanded(expanded: true, persist: true);
            await RefreshWorkspaceCoreAsync();
            UpdateStatus($"已打开工作区：{root}");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法打开文件夹", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private void CloseWorkspace_Click(object sender, RoutedEventArgs eventArgs)
    {
        workspaceRootPath = null;
        selectedWorkspaceItem = null;
        WorkspaceItems.Clear();
        WorkspacePaneContent.Visibility = Visibility.Collapsed;
        RecentPaneContent.Visibility = Visibility.Visible;
        CloseWorkspaceMenuItem.IsEnabled = false;
        UpdateStatus("已关闭文件夹，当前文档保持打开");
    }

    private async void RefreshWorkspace_Click(object sender, RoutedEventArgs eventArgs)
    {
        await RefreshWorkspaceAsync();
    }

    private async Task RefreshWorkspaceAsync()
    {
        if (workspaceRootPath is null || workspaceOperationRunning)
        {
            return;
        }

        workspaceOperationRunning = true;
        try
        {
            await RefreshWorkspaceCoreAsync();
            UpdateStatus("工作区已刷新");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法刷新工作区", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private async Task RefreshWorkspaceCoreAsync()
    {
        string root = workspaceRootPath
            ?? throw new InvalidOperationException("尚未打开工作区。");
        IReadOnlyList<WorkspaceEntry> entries = await Task.Run(
            () => workspaceFileService.GetChildren(root, root));
        WorkspaceItems.Clear();
        foreach (WorkspaceEntry entry in entries)
        {
            WorkspaceItems.Add(new WorkspaceTreeItemViewModel(entry));
        }

        WorkspaceEmptyState.Visibility = WorkspaceItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void WorkspaceTree_Expanded(object sender, RoutedEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not TreeViewItem
            {
                DataContext: WorkspaceTreeItemViewModel node,
            }
            || !node.IsDirectory
            || node.IsLoaded
            || workspaceRootPath is null)
        {
            return;
        }

        try
        {
            string root = workspaceRootPath;
            IReadOnlyList<WorkspaceEntry> entries = await Task.Run(
                () => workspaceFileService.GetChildren(root, node.Path));
            node.ReplaceChildren(entries);
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            node.ReplaceChildren([]);
            ShowWorkspaceError("无法读取目录", exception);
        }
    }

    private void WorkspaceTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> eventArgs)
    {
        selectedWorkspaceItem = eventArgs.NewValue as WorkspaceTreeItemViewModel;
    }

    private async void WorkspaceTree_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter
            && selectedWorkspaceItem is { IsFile: true } item)
        {
            eventArgs.Handled = true;
            await OpenWorkspaceDocumentAsync(item.Path);
        }
    }

    private async void WorkspaceTree_MouseDoubleClick(object sender, MouseButtonEventArgs eventArgs)
    {
        TreeViewItem? container = ItemsControl.ContainerFromElement(
            WorkspaceTree,
            eventArgs.OriginalSource as DependencyObject) as TreeViewItem;
        if (container?.DataContext is WorkspaceTreeItemViewModel
            {
                IsDirectory: false,
                IsPlaceholder: false,
            } item)
        {
            eventArgs.Handled = true;
            await OpenWorkspaceDocumentAsync(item.Path);
        }
    }

    private async void OpenWorkspaceEntry_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item)
            && !item.IsDirectory)
        {
            await OpenWorkspaceDocumentAsync(item.Path);
        }
    }

    private async Task OpenWorkspaceDocumentAsync(string path)
    {
        if (await ConfirmDiscardOrSaveAsync())
        {
            await OpenDocumentAsync(path);
        }
    }

    private async void NewWorkspaceFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? parent = GetWorkspaceParentDirectory(sender);
        if (parent is not null)
        {
            await CreateWorkspaceFileAsync(parent);
        }
    }

    private async void NewWorkspaceRootFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        // A blank-area command intentionally targets the workspace root instead of
        // reusing a previously selected node, which would make the result surprising.
        string? root = workspaceRootPath;
        if (root is not null)
        {
            await CreateWorkspaceFileAsync(root);
        }
    }

    private async Task CreateWorkspaceFileAsync(string parentDirectory)
    {
        if (workspaceRootPath is null || workspaceOperationRunning)
        {
            return;
        }

        WorkspaceNameDialog dialog = new(
            "新建 Markdown 文件",
            "输入文件名；未填写扩展名时自动使用 .md。")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || !await ConfirmDiscardOrSaveAsync())
        {
            return;
        }

        // WPF controls are thread-affine. Capture dialog input before entering
        // Task.Run so the worker receives only immutable strings and pure file I/O.
        string enteredName = dialog.EnteredName;
        workspaceOperationRunning = true;
        try
        {
            string root = workspaceRootPath;
            string path = await Task.Run(
                () => workspaceFileService.CreateMarkdownFile(
                    root,
                    parentDirectory,
                    enteredName));
            await RefreshWorkspaceCoreAsync();
            await OpenDocumentAsync(path);
            UpdateStatus($"已新建文件：{path}");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法新建文件", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private async void NewWorkspaceDirectory_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? parent = GetWorkspaceParentDirectory(sender);
        if (parent is not null)
        {
            await CreateWorkspaceDirectoryAsync(parent);
        }
    }

    private async void NewWorkspaceRootDirectory_Click(object sender, RoutedEventArgs eventArgs)
    {
        // Blank-area commands always operate on the root. A stale selection must not
        // redirect a root-level action into a previously selected child directory.
        string? root = workspaceRootPath;
        if (root is not null)
        {
            await CreateWorkspaceDirectoryAsync(root);
        }
    }

    private async Task CreateWorkspaceDirectoryAsync(string parentDirectory)
    {
        if (workspaceRootPath is null || workspaceOperationRunning)
        {
            return;
        }

        WorkspaceNameDialog dialog = new("新建文件夹", "输入文件夹名称。")
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // WPF controls are thread-affine. The worker may receive the copied string,
        // but it must never read NameTextBox through dialog.EnteredName itself.
        string enteredName = dialog.EnteredName;
        workspaceOperationRunning = true;
        try
        {
            string root = workspaceRootPath;
            string path = await Task.Run(
                () => workspaceFileService.CreateDirectory(root, parentDirectory, enteredName));
            await RefreshWorkspaceCoreAsync();
            UpdateStatus($"已新建文件夹：{path}");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法新建文件夹", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private async void RenameWorkspaceEntry_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item)
            || workspaceRootPath is null
            || workspaceOperationRunning)
        {
            return;
        }

        WorkspaceNameDialog dialog = new("重命名", $"为“{item.Name}”输入新名称。", item.Name)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string enteredName = dialog.EnteredName;
        string? currentRelativePath = GetRelativePathWhenContained(document.FilePath, item.Path);
        if (currentRelativePath is not null && !await ConfirmDiscardOrSaveAsync())
        {
            return;
        }

        workspaceOperationRunning = true;
        try
        {
            string root = workspaceRootPath;
            string target = await Task.Run(
                () => workspaceFileService.Rename(root, item.Path, enteredName));
            applicationSettings = applicationSettings.RelocateRecentFiles(item.Path, target);
            RefreshRecentFilesView();
            TrySaveApplicationSettings();
            await RefreshWorkspaceCoreAsync();
            if (currentRelativePath is not null)
            {
                string relocatedDocument = currentRelativePath.Length == 0
                    ? target
                    : Path.Combine(target, currentRelativePath);
                await OpenDocumentAsync(relocatedDocument);
            }

            UpdateStatus($"已重命名为：{target}");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法重命名", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private async void DeleteWorkspaceEntry_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item)
            || workspaceRootPath is null
            || workspaceOperationRunning)
        {
            return;
        }

        bool containsCurrentDocument = GetRelativePathWhenContained(
            document.FilePath,
            item.Path) is not null;
        string itemKind = item.IsDirectory ? "文件夹及其全部内容" : "文件";
        string dirtyWarning = containsCurrentDocument && document.IsDirty
            ? "\n\n当前编辑内容尚未保存，删除后这些修改也会丢失。"
            : string.Empty;
        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"此操作将删除磁盘上的实际{itemKind}，确定继续吗？\n\n{item.Path}{dirtyWarning}\n\n删除后无法通过 WIMD 撤销。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        workspaceOperationRunning = true;
        try
        {
            string root = workspaceRootPath;
            await Task.Run(() => workspaceFileService.Delete(root, item.Path));
            applicationSettings = applicationSettings.RemoveRecentFilesAtOrBelow(item.Path);
            RefreshRecentFilesView();
            TrySaveApplicationSettings();
            if (containsCurrentDocument)
            {
                document.StartNew(++untitledCounter);
                ApplyDocumentToEditor();
            }

            await RefreshWorkspaceCoreAsync();
            UpdateStatus($"已从磁盘删除：{item.Path}");
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            ShowWorkspaceError("无法删除", exception);
        }
        finally
        {
            workspaceOperationRunning = false;
        }
    }

    private void RevealWorkspaceRoot_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? root = workspaceRootPath;
        if (root is not null)
        {
            RevealWorkspacePath(root, "工作区根目录");
        }
    }

    private async void CopyWorkspaceRootPath_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? root = workspaceRootPath;
        if (root is not null)
        {
            await CopyRecentValueAsync(root, "工作区根目录路径");
        }
    }

    private void RevealWorkspaceEntry_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item))
        {
            RevealWorkspacePath(item.Path, "工作区条目");
        }
    }

    private void RevealWorkspacePath(string path, string description)
    {
        try
        {
            fileExplorerService.RevealPath(path);
            UpdateStatus($"已在文件资源管理器中显示{description}");
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or FileNotFoundException
            or InvalidOperationException
            or Win32Exception)
        {
            ShowWorkspaceError("无法打开文件资源管理器", exception);
        }
    }

    private async void CopyWorkspacePath_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item))
        {
            await CopyRecentValueAsync(item.Path, "工作区路径");
        }
    }

    private string? GetWorkspaceParentDirectory(object sender)
    {
        if (TryGetWorkspaceItem(sender, out WorkspaceTreeItemViewModel item))
        {
            return item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path);
        }

        if (selectedWorkspaceItem is not null)
        {
            return selectedWorkspaceItem.IsDirectory
                ? selectedWorkspaceItem.Path
                : Path.GetDirectoryName(selectedWorkspaceItem.Path);
        }

        return workspaceRootPath;
    }

    private static bool TryGetWorkspaceItem(
        object sender,
        out WorkspaceTreeItemViewModel item)
    {
        if (sender is FrameworkElement
            {
                Tag: WorkspaceTreeItemViewModel taggedItem,
            }
            && !taggedItem.IsPlaceholder)
        {
            item = taggedItem;
            return true;
        }

        item = null!;
        return false;
    }

    private static string? GetRelativePathWhenContained(string? candidatePath, string containerPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return null;
        }

        string candidate = Path.GetFullPath(candidatePath);
        string container = Path.GetFullPath(containerPath);
        if (string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (!Directory.Exists(container))
        {
            return null;
        }

        string relative = Path.GetRelativePath(container, candidate);
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            ? relative
            : null;
    }

    private static bool IsWorkspaceFailure(Exception exception)
    {
        return exception is WorkspaceFileException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or InvalidOperationException;
    }

    private void ShowWorkspaceError(string title, Exception exception)
    {
        UpdateStatus($"{title}：{exception.Message}");
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
