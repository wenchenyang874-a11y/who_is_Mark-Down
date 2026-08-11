using System.Globalization;
using System.IO;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.ViewModels;

public sealed class RecentFileItemViewModel
{
    public RecentFileItemViewModel(RecentFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Path = entry.Path;
        DisplayName = System.IO.Path.GetFileName(entry.Path);
        DirectoryPath = System.IO.Path.GetDirectoryName(entry.Path) ?? entry.Path;
        LastOpenedDisplay = entry.LastOpenedUtc
            .ToLocalTime()
            .ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        IsAvailable = File.Exists(entry.Path);
    }

    public string Path { get; }

    public string DisplayName { get; }

    public string DirectoryPath { get; }

    public string LastOpenedDisplay { get; }

    public bool IsAvailable { get; }
}
