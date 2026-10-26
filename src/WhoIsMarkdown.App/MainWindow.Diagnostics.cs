using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WhoIsMarkdown.App.Services;

namespace WhoIsMarkdown.App;

/// <summary>
/// Correlate production window phases without recording a document identity or
/// changing preview timing. Test hosts opt into an isolated logger explicitly.
/// </summary>
public partial class MainWindow
{
    private readonly int diagnosticWindowId = DiagnosticLog.Current?.AllocateWindowId() ?? 0;
    private bool firstPreviewLogged;
    private bool firstFrameLogged;
    private long detailedRenderGeneration = 1;
    private long lastDetailedRenderGeneration;

    protected override void OnContentRendered(EventArgs e)
    {
        if (!firstFrameLogged)
        {
            firstFrameLogged = true;
            DiagnosticLog.Current?.Mark(DiagnosticStage.WindowFirstFrame, diagnosticWindowId);
        }
        base.OnContentRendered(e);
    }

    private async void OpenLogDirectory_Click(object sender, RoutedEventArgs eventArgs)
    {
        LocalDiagnosticLog? log = DiagnosticLog.Current;
        if (log is null) return;
        try
        {
            string directory = log.DirectoryPath;
            await Task.Run(() => Directory.CreateDirectory(directory));
            ProcessStartInfo start = new("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(directory);
            using Process? process = Process.Start(start);
            if (log.FailureType is { } failure)
                UpdateStatus($"日志目录已打开；本次日志写入失败（{failure}），请检查磁盘空间及权限。");
            else UpdateStatus("已打开本地诊断日志目录；日志不会自动上传。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException)
        {
            log.Error(DiagnosticStage.OpenLogDirectory, exception, diagnosticWindowId);
            UpdateStatus($"无法打开日志目录（{exception.GetType().Name}）。");
        }
    }

    private void CaptureStartupDiagnostic_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(this,
            "将请求管理员授权，使用 Windows 自带 WPR 录制最长 30 秒系统性能跟踪，并以普通权限新开一个 WIMD 窗口。\n\n"
            + "新窗口打开当前文档的磁盘版本；未保存内容不会复制或覆盖，无路径时打开空白窗口。"
            + "跟踪覆盖进程创建前后的线程等待、CPU 调用栈、程序集及文件访问；就绪后提前停止。合并保存可能更久，需预留至少 6 GB 空间。"
            + "不会关闭现有窗口、清空缓存或修改安全软件。\n\n"
            + "原始 ETL 可能包含本机路径、命令行及其他进程元数据，仅保存到本地日志目录的 Traces 子目录，不自动上传。"
            + "采集有开销，结束后会自动停止并打开结果目录。是否继续？",
            "采集启动诊断", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        try
        {
            using Process? process = Process.Start(StartupDiagnosticLauncher.CreateStartInfo(AppContext.BaseDirectory, document.FilePath));
            UpdateStatus("启动诊断已请求；请确认 UAC，结束后自动打开本地结果目录。现有窗口不受影响。");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Current?.Error(DiagnosticStage.StartupTraceLaunch, exception, diagnosticWindowId);
            UpdateStatus($"无法启动系统诊断（{exception.GetType().Name}）。");
        }
    }
}
