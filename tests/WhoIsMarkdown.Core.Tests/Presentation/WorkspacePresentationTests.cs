namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class WorkspacePresentationTests
{
    [Fact]
    public void 主窗口_文件夹模式入口与磁盘操作_保持可发现且语义明确()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xamlPath = Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);

        Assert.Contains("Header=\"打开文件夹...\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"工作区文件树\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding WorkspaceItems, ElementName=RootWindow}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Header=\"重命名\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"删除\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"删除实际文件或文件夹\"", xaml, StringComparison.Ordinal);
        string codeBehind = File.ReadAllText(Path.Combine(repositoryRoot, "src", "WhoIsMarkdown.App", "MainWindow.Workspace.cs"));
        Assert.Contains("此操作将删除磁盘上的实际", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Header=\"移出最近记录（不删除文件）\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 主窗口_侧栏收起后_保留可见的展开入口()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.RecentFiles.cs"));

        Assert.Contains("x:Name=\"ExpandRecentPaneButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"展开侧栏\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ExpandRecentPane_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RecentPaneGutterColumn.Width", codeBehind, StringComparison.Ordinal);
        Assert.Contains(
            "ExpandRecentPaneButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 主窗口_工作区文件操作_进入后台线程前复制对话框输入()
    {
        string repositoryRoot = FindRepositoryRoot();
        string codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Workspace.cs"));

        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Count(
                codeBehind,
                @"string enteredName = dialog\.EnteredName;"));
        Assert.DoesNotContain("dialog.EnteredName));", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void 主窗口_工作区空白区域_提供根目录上下文操作()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Workspace.cs"));

        Assert.Contains(
            "AutomationProperties.Name=\"工作区根目录菜单\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Click=\"NewWorkspaceRootFile_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"NewWorkspaceRootDirectory_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RefreshWorkspace_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RevealWorkspaceRoot_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyWorkspaceRootPath_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<TreeView.ContextMenu>", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "private async void NewWorkspaceRootFile_Click",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains("await CreateWorkspaceFileAsync(root);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await CreateWorkspaceDirectoryAsync(root);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RevealWorkspacePath(root, \"工作区根目录\");", codeBehind, StringComparison.Ordinal);
        Assert.Contains("await CopyRecentValueAsync(root, \"工作区根目录路径\");", codeBehind, StringComparison.Ordinal);
    }
    [Fact]
    public void 主窗口_工作区树交互_双击切换与右键选中保持一致()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string workspaceCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Workspace.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "PreviewMouseRightButtonDown=\"WorkspaceTree_PreviewMouseRightButtonDown\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Value=\"#ECE9FF\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "MouseLeftButtonDown=\"WorkspaceTreeItem_MouseLeftButtonDown\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MouseDoubleClick=", xaml, StringComparison.Ordinal);
        Assert.Contains("eventArgs.ChangedButton != MouseButton.Left", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("eventArgs.ClickCount != 2", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("WorkspaceTreeItem_MouseLeftButtonDown", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("FindWorkspaceTreeItem", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("container.IsSelected = true;", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent(source)", workspaceCode, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref documentOpenVersion)", windowCode, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref documentOpenVersion)", windowCode, StringComparison.Ordinal);
    }

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
