namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Represents user-scoped, non-document application preferences. The settings
/// contain paths only and are persisted locally; document contents are never stored.
/// </summary>
public sealed record ApplicationSettings
{
    public const int MaximumRecentFiles = 10;
    public const double DefaultBackgroundOpacity = 0.18;

    public IReadOnlyList<RecentFileEntry> RecentFiles { get; init; } = [];

    public string? BackgroundImagePath { get; init; }

    public double BackgroundOpacity { get; init; } = DefaultBackgroundOpacity;

    public bool IsRecentPaneExpanded { get; init; } = true;

    public IReadOnlyDictionary<string, ShortcutGesture> ShortcutOverrides { get; init; }
        = new Dictionary<string, ShortcutGesture>(StringComparer.Ordinal);

    public ApplicationSettings Normalize()
    {
        List<RecentFileEntry> recentFiles = [];
        HashSet<string> knownPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecentFileEntry entry in RecentFiles ?? [])
        {
            string? normalizedPath = TryNormalizePath(entry.Path);
            if (normalizedPath is null || !knownPaths.Add(normalizedPath))
            {
                continue;
            }

            recentFiles.Add(new RecentFileEntry(normalizedPath, entry.LastOpenedUtc));
        }

        IReadOnlyList<RecentFileEntry> normalizedRecentFiles = recentFiles
            .OrderByDescending(entry => entry.LastOpenedUtc)
            .Take(MaximumRecentFiles)
            .ToArray();

        Dictionary<string, ShortcutGesture> normalizedShortcuts = new(StringComparer.Ordinal);
        foreach ((string commandId, ShortcutGesture gesture) in ShortcutOverrides ??
                 new Dictionary<string, ShortcutGesture>())
        {
            ShortcutGesture normalizedGesture = gesture?.Normalize()
                ?? new ShortcutGesture { Key = string.Empty };
            if (!string.IsNullOrWhiteSpace(commandId)
                && !string.IsNullOrWhiteSpace(normalizedGesture.Key))
            {
                normalizedShortcuts[commandId.Trim()] = normalizedGesture;
            }
        }

        return this with
        {
            RecentFiles = normalizedRecentFiles,
            BackgroundImagePath = TryNormalizePath(BackgroundImagePath),
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1),
            ShortcutOverrides = normalizedShortcuts,
        };
    }

    public ApplicationSettings RecordRecentFile(string path, DateTimeOffset openedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        ApplicationSettings normalizedSettings = Normalize();

        RecentFileEntry[] updatedEntries =
        [
            new(normalizedPath, openedAtUtc),
            .. normalizedSettings.RecentFiles.Where(
                entry => !string.Equals(entry.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)),
        ];

        return normalizedSettings with
        {
            RecentFiles = updatedEntries.Take(MaximumRecentFiles).ToArray(),
        };
    }

    public ApplicationSettings RemoveRecentFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        ApplicationSettings normalizedSettings = Normalize();

        return normalizedSettings with
        {
            RecentFiles = normalizedSettings.RecentFiles
                .Where(entry => !string.Equals(
                    entry.Path,
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray(),
        };
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
