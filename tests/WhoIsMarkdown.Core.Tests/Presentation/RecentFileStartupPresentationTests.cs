namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class RecentFileStartupPresentationTests
{
    [Fact]
    public void 最近文件投影_启动阶段_不访问文件系统()
    {
        string repositoryRoot = FindRepositoryRoot();
        string itemViewModel = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ViewModels",
            "RecentFileItemViewModel.cs"));
        string recentFilesCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.RecentFiles.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));

        Assert.DoesNotContain("File.Exists(", itemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(", itemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAvailable", itemViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding IsAvailable}", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => File.Exists(path))", recentFilesCode, StringComparison.Ordinal);
        Assert.Contains(
            "await Task.Run(() => fileService.ReadAsync(path))",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.Contains("GetStartupPathArgument()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains(
            ".FirstOrDefault(argument => !argument.StartsWith(\"--\", StringComparison.Ordinal))",
            mainWindowCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GetStartupWorkspacePath", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GetStartupDocumentPath", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(path)", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("startupDocumentPath is not null && File.Exists", mainWindowCode, StringComparison.Ordinal);
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
