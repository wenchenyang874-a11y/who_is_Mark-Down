using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class UpdateRestartAndNewWindowPresentationTests
{
    [Fact]
    public void 安装更新_运行检测与完成页恢复选项_使用同一一次性协议()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "App.xaml.cs"));
        string installer = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "WIMD.iss"));

        const string mutexName =
            @"Local\WIMD.UpdateRestart.A278F55A4D4146B4A1D9DA41D8C7D655";
        Assert.Contains(mutexName, appCode, StringComparison.Ordinal);
        Assert.Contains(mutexName, installer, StringComparison.Ordinal);
        Assert.Contains("CheckForMutexes(WimdRunningMutexName)", installer, StringComparison.Ordinal);
        Assert.Contains("WriteRestartRequest('capture')", installer, StringComparison.Ordinal);
        Assert.Contains("WriteRestartRequest('restore')", installer, StringComparison.Ordinal);
        Assert.Contains("Description: \"重新打开并恢复 WIMD 窗口\"", installer, StringComparison.Ordinal);
        Assert.Contains("Parameters: \"--restore-update-session\"", installer, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"重新打开并恢复 WIMD 窗口[^\r\n]*unchecked", RegexOptions.IgnoreCase),
            installer);
    }

    [Fact]
    public void 安装更新_Wimd正在运行_只显示内置关闭应用确认()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "WIMD.iss"));
        string language = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "packaging",
            "languages",
            "ChineseSimplified.isl"));

        Assert.Contains("function PrepareToInstall(var NeedsRestart: Boolean): String;", installer, StringComparison.Ordinal);
        Assert.Contains("WriteRestartRequest('capture')", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("function ConfirmCloseAndInstall", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("function NextButtonClick", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("“关闭并安装”说明", installer, StringComparison.Ordinal);
        Assert.Contains("选择“关闭 WIMD 并安装”后", language, StringComparison.Ordinal);
        Assert.Contains("重新打开并恢复 WIMD 窗口", language, StringComparison.Ordinal);
    }

    [Fact]
    public void 安装更新_未保存正文先写入临时恢复区且恢复后仍为未保存()
    {
        string repositoryRoot = FindRepositoryRoot();
        string commandsCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Commands.cs"));
        string restartCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Restart.cs"));
        string documentViewModel = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ViewModels",
            "DocumentEditorViewModel.cs"));
        string updateCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Updates.cs"));

        Assert.Contains("TryPersistUpdateRestartWindowState()", commandsCode, StringComparison.Ordinal);
        Assert.True(
            commandsCode.IndexOf("TryPersistUpdateRestartWindowState()", StringComparison.Ordinal)
            < commandsCode.IndexOf("if (closeApproved || !document.IsDirty)", StringComparison.Ordinal));
        Assert.Contains("ConfirmDiscardOrSaveAsync()", commandsCode, StringComparison.Ordinal);
        Assert.Contains("document.AddDocumentRecoveryTo", restartCode, StringComparison.Ordinal);
        Assert.Contains("SavedDocumentText = includeContent ? savedText : null", documentViewModel, StringComparison.Ordinal);
        Assert.Contains("savedText = normalized.SavedDocumentText", documentViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("closeApproved = true;\n            Close();", updateCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 安装更新_恢复请求使用Unicode安全写入避免运行时类型不匹配()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "WIMD.iss"));

        Assert.Contains("GetDateTimeString('yyyymmddhhnnss', #0, #0)", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDateTimeString('yyyymmddhhnnss', '', '')", installer, StringComparison.Ordinal);
        Assert.Contains("TArrayOfString", installer, StringComparison.Ordinal);
        Assert.Contains("SetArrayLength(RequestLines, 1)", installer, StringComparison.Ordinal);
        Assert.Contains("SaveStringsToUTF8FileWithoutBOM(", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveStringToFile(", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void 最近文件与工作区文件_均提供安全的新窗口打开命令()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string launcher = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "ApplicationWindowLauncher.cs"));

        Assert.Equal(2, Regex.Count(xaml, "Header=\"在新窗口中打开\""));
        Assert.Contains("OpenRecentFileInNewWindow_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenWorkspaceEntryInNewWindow_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"--new-window\")", launcher, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(normalizedPath)", launcher, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = false", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows资源管理器_文件夹右键菜单_使用带引号的路径启动工作区()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "WIMD.iss"));
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "Subkey: \"Software\\Classes\\Directory\\shell\\WIMD\"; ValueType: string; ValueData: \"用 WIMD 打开\"; Flags: uninsdeletekey",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Subkey: \"Software\\Classes\\Directory\\shell\\WIMD\"; ValueType: string; ValueName: \"Icon\"; ValueData: \"{app}\\{#MyAppExeName},0\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Subkey: \"Software\\Classes\\Directory\\shell\\WIMD\\command\"; ValueType: string; ValueData: \"\"\"{app}\\{#MyAppExeName}\"\" \"\"%1\"\"\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("GetStartupWorkspacePath()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FirstOrDefault(Directory.Exists)", mainWindow, StringComparison.Ordinal);
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
