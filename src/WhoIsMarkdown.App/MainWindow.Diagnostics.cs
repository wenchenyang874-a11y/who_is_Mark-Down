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
}
