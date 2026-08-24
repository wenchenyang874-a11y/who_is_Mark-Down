using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class StatusBarPerformancePresentationTests
{
    [Fact]
    public void 状态栏_显示当前进程Cpu和工作集口径()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));

        XDocument document = XDocument.Parse(mainWindow);
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement performanceText = Assert.Single(
            document.Descendants(),
            element => (string?)element.Attribute(xNamespace + "Name") == "PerformanceText");

        Assert.Equal("当前 WIMD 性能占用", (string?)performanceText.Attribute("AutomationProperties.Name"));
        Assert.Contains("CPU 0.0%", (string?)performanceText.Attribute("Text"), StringComparison.Ordinal);
        Assert.Contains("每秒刷新", (string?)performanceText.Attribute("ToolTip"), StringComparison.Ordinal);
        Assert.Contains("不包含 WebView2 子进程", (string?)performanceText.Attribute("ToolTip"), StringComparison.Ordinal);
    }

    [Fact]
    public void 状态栏_性能采样随主窗口创建和关闭管理生命周期()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));
        string commandsCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Commands.cs"));
        string performanceCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Performance.cs"));

        Assert.Contains("InitializePerformanceMonitor();", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("DisposePerformanceMonitor();", commandsCode, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(1)", performanceCode, StringComparison.Ordinal);
        Assert.Contains("Process.GetCurrentProcess()", performanceCode, StringComparison.Ordinal);
        Assert.Contains("process.WorkingSet64", performanceCode, StringComparison.Ordinal);
        Assert.Contains("Environment.ProcessorCount", performanceCode, StringComparison.Ordinal);
        Assert.Contains("performanceProcess?.Dispose()", performanceCode, StringComparison.Ordinal);
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
