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
        Assert.Contains("Header=\"删除磁盘内容...\"", xaml, StringComparison.Ordinal);
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
