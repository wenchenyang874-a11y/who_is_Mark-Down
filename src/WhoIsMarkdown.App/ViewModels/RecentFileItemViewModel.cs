using System.Globalization;
using System.IO;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.ViewModels;

public sealed class RecentFileItemViewModel
{
    public RecentFileItemViewModel(RecentFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RecentFileActionTargets targets = RecentFileActionTargets.Create(entry.Path);
        Path = targets.FilePath;
        DisplayName = System.IO.Path.GetFileName(targets.FilePath);
        DirectoryPath = targets.DirectoryPath;
        LastOpenedDisplay = entry.LastOpenedUtc
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        IsAvailable = File.Exists(targets.FilePath);
    }

    public string Path { get; }

    public string DisplayName { get; }

    public string DirectoryPath { get; }

    public string LastOpenedDisplay { get; }

    public bool IsAvailable { get; }
}
