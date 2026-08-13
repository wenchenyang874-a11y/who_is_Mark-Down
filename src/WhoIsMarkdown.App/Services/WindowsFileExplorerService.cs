using System.Diagnostics;
using System.IO;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Opens Windows File Explorer with an existing file or directory selected.
/// ArgumentList keeps Chinese, spaces, and punctuation as path data instead of
/// constructing an executable command string.
/// </summary>
public sealed class WindowsFileExplorerService : IFileExplorerService
{
    public void RevealFile(string path)
    {
        RecentFileActionTargets targets = RecentFileActionTargets.Create(path);
        RevealPath(targets.FilePath);
    }

    public void RevealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The workspace entry no longer exists.", fullPath);
        }

        string explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = explorerPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"/select,{fullPath}");

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows File Explorer could not be started.");
        }
    }
}
