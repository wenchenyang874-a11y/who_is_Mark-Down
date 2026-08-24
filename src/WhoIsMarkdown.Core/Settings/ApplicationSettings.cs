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

    public ImageInsertionSettings ImageInsertion { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public bool CheckForUpdatesOnStartup { get; init; }

    public IReadOnlyDictionary<string, ShortcutGesture> ShortcutOverrides { get; init; }
        = new Dictionary<string, ShortcutGesture>(StringComparer.Ordinal);

    public ApplicationSettings Normalize()
    {
        return Normalize(sortRecentFiles: true);
    }

    /// <summary>
    /// Normalizes persisted values without changing the current session's recent-file order.
    /// The next application load calls <see cref="Normalize()"/> and applies the latest order.
    /// </summary>
    internal ApplicationSettings NormalizeForPersistence()
    {
        return Normalize(sortRecentFiles: false);
    }

    private ApplicationSettings Normalize(bool sortRecentFiles)
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

        IEnumerable<RecentFileEntry> normalizedRecentFileSequence = sortRecentFiles
            ? recentFiles.OrderByDescending(entry => entry.LastOpenedUtc)
            : recentFiles;
        IReadOnlyList<RecentFileEntry> normalizedRecentFiles = normalizedRecentFileSequence
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
            ImageInsertion = (ImageInsertion ?? new ImageInsertionSettings()).Normalize(),
            Appearance = (Appearance ?? new AppearanceSettings()).Normalize(),
            ShortcutOverrides = normalizedShortcuts,
        };
    }

    public ApplicationSettings RecordRecentFile(string path, DateTimeOffset openedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        ApplicationSettings normalizedSettings = NormalizeForPersistence();
        List<RecentFileEntry> updatedEntries = [.. normalizedSettings.RecentFiles];
        int existingIndex = updatedEntries.FindIndex(
            entry => string.Equals(entry.Path, normalizedPath, StringComparison.OrdinalIgnoreCase));

        // Keep existing items at their current indices while WIMD is running. Moving
        // the clicked item immediately made the recent-file targets jump under the
        // pointer. The refreshed timestamp is sorted only when settings are loaded
        // during the next application startup. Brand-new paths still appear at the
        // front so users can immediately see that the file was recorded.
        if (existingIndex >= 0)
        {
            updatedEntries[existingIndex] = new RecentFileEntry(normalizedPath, openedAtUtc);
        }
        else
        {
            updatedEntries.Insert(0, new RecentFileEntry(normalizedPath, openedAtUtc));
        }

        return normalizedSettings with
        {
            RecentFiles = updatedEntries.Take(MaximumRecentFiles).ToArray(),
        };
    }

    public ApplicationSettings RemoveRecentFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        ApplicationSettings normalizedSettings = NormalizeForPersistence();

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

    public ApplicationSettings RemoveRecentFilesAtOrBelow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        ApplicationSettings normalizedSettings = NormalizeForPersistence();

        return normalizedSettings with
        {
            RecentFiles = normalizedSettings.RecentFiles
                .Where(entry => GetRelativePathWhenContained(entry.Path, normalizedPath) is null)
                .ToArray(),
        };
    }

    public ApplicationSettings RelocateRecentFiles(string sourcePath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string normalizedSource = System.IO.Path.GetFullPath(sourcePath);
        string normalizedTarget = System.IO.Path.GetFullPath(targetPath);
        ApplicationSettings normalizedSettings = NormalizeForPersistence();

        RecentFileEntry[] relocatedEntries = normalizedSettings.RecentFiles
            .Select(entry =>
            {
                string? relative = GetRelativePathWhenContained(entry.Path, normalizedSource);
                if (relative is null)
                {
                    return entry;
                }

                string relocatedPath = relative.Length == 0
                    ? normalizedTarget
                    : System.IO.Path.Combine(normalizedTarget, relative);
                return entry with { Path = relocatedPath };
            })
            .ToArray();

        return (normalizedSettings with { RecentFiles = relocatedEntries })
            .NormalizeForPersistence();
    }

    private static string? GetRelativePathWhenContained(string candidatePath, string containerPath)
    {
        if (string.Equals(candidatePath, containerPath, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string relative = System.IO.Path.GetRelativePath(containerPath, candidatePath);
        return !System.IO.Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                string.Concat("..", System.IO.Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            && !relative.StartsWith(
                string.Concat("..", System.IO.Path.AltDirectorySeparatorChar),
                StringComparison.Ordinal)
            ? relative
            : null;
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
