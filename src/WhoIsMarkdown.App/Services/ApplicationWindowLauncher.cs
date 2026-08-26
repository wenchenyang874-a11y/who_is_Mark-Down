using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WhoIsMarkdown.App.Services;

public interface IApplicationWindowLauncher
{
    public void OpenDocumentInNewWindow(string path);
}

/// <summary>
/// Starts an independent WIMD process with one validated Markdown path. Each
/// argument is passed separately so a file name can never become shell syntax.
/// </summary>
public sealed class ApplicationWindowLauncher : IApplicationWindowLauncher
{
    public void OpenDocumentInNewWindow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = Path.GetFullPath(path);
        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException("找不到要在新窗口中打开的文件。", normalizedPath);
        }

        string extension = Path.GetExtension(normalizedPath);
        if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("只能在新窗口中打开 Markdown 文件。");
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定 WIMD 程序路径。");
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(normalizedPath);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Windows 未能启动新的 WIMD 窗口。");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Windows 未能启动新的 WIMD 窗口。", exception);
        }
    }
}
