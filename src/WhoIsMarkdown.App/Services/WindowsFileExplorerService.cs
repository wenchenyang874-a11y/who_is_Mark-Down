using System.Diagnostics;
using System.IO;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Opens Windows File Explorer with a recent file selected. ArgumentList keeps the
/// path as data, including Chinese text and spaces, instead of constructing a shell command.
/// </summary>
public sealed class WindowsFileExplorerService : IFileExplorerService
{
    public void RevealFile(string path)
    {
        RecentFileActionTargets targets = RecentFileActionTargets.Create(path);
        if (!File.Exists(targets.FilePath))
        {
            throw new FileNotFoundException("The recent file no longer exists.", targets.FilePath);
        }

        string explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = explorerPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"/select,{targets.FilePath}");

        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows File Explorer could not be started.");
        }
    }
}
