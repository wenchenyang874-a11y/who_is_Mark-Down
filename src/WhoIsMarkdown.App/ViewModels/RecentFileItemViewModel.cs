using System.Globalization;
using System.IO;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.ViewModels;

public sealed class RecentFileItemViewModel
{
    public RecentFileItemViewModel(RecentFileEntry entry, string? currentDocumentPath)
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
        IsCurrent = IsCurrentDocument(targets.FilePath, currentDocumentPath);
    }

    public string Path { get; }

    public string DisplayName { get; }

    public string DirectoryPath { get; }

    public string LastOpenedDisplay { get; }

    public bool IsAvailable { get; }

    public bool IsCurrent { get; }

    private static bool IsCurrentDocument(string recentFilePath, string? currentDocumentPath)
    {
        // The active marker is transient UI state. Compare normalized Windows paths
        // without changing the persisted recent-file order or writing extra settings.
        if (string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            return false;
        }

        try
        {
            string normalizedCurrentPath = System.IO.Path.GetFullPath(currentDocumentPath);
            return string.Equals(
                recentFilePath,
                normalizedCurrentPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }
}
