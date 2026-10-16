namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Bug fix: saving a stale window's whole preferences erased other windows' edits.
/// Merge only differences from that window's acknowledged baseline. Scalar conflicts
/// use the last committed change; recent files merge by stable key. Trust policy
/// and shortcut assignments are atomic groups to avoid manufacturing unsafe policy
/// combinations or conflicting shortcuts from independently validated windows.
/// </summary>
public static class ApplicationSettingsChanges
{
    public static ApplicationSettings Capture(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.NormalizeForPersistence();
    }

    public static ApplicationSettings Merge(ApplicationSettings persisted, ApplicationSettings baseline, ApplicationSettings current)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ApplicationSettings before = Capture(baseline);
        ApplicationSettings after = Capture(current);
        ApplicationSettings disk = Capture(persisted);
        AppearanceSettings oldAppearance = before.Appearance;
        AppearanceSettings newAppearance = after.Appearance;
        ImageInsertionSettings oldImages = before.ImageInsertion;
        ImageInsertionSettings newImages = after.ImageInsertion;
        bool policyChanged = oldImages.TrustMode != newImages.TrustMode
            || !oldImages.RemoteImageRules.SequenceEqual(newImages.RemoteImageRules);
        bool shortcutsChanged = before.ShortcutOverrides.Count != after.ShortcutOverrides.Count
            || before.ShortcutOverrides.Any(item => !after.ShortcutOverrides.TryGetValue(item.Key, out ShortcutGesture? value) || item.Value != value);
        return disk with
        {
            BackgroundImagePath = Select(disk.BackgroundImagePath, before.BackgroundImagePath, after.BackgroundImagePath),
            BackgroundOpacity = Select(disk.BackgroundOpacity, before.BackgroundOpacity, after.BackgroundOpacity),
            IsRecentPaneExpanded = Select(disk.IsRecentPaneExpanded, before.IsRecentPaneExpanded, after.IsRecentPaneExpanded),
            CheckForUpdatesOnStartup = Select(disk.CheckForUpdatesOnStartup, before.CheckForUpdatesOnStartup, after.CheckForUpdatesOnStartup),
            FileOpenViewMode = Select(disk.FileOpenViewMode, before.FileOpenViewMode, after.FileOpenViewMode),
            LastViewMode = Select(disk.LastViewMode, before.LastViewMode, after.LastViewMode),
            Appearance = disk.Appearance with
            {
                Theme = Select(disk.Appearance.Theme, oldAppearance.Theme, newAppearance.Theme),
                EditorFontFamily = Select(disk.Appearance.EditorFontFamily, oldAppearance.EditorFontFamily, newAppearance.EditorFontFamily),
                EditorFontSize = Select(disk.Appearance.EditorFontSize, oldAppearance.EditorFontSize, newAppearance.EditorFontSize),
                PreviewFontFamily = Select(disk.Appearance.PreviewFontFamily, oldAppearance.PreviewFontFamily, newAppearance.PreviewFontFamily),
                PreviewFontSize = Select(disk.Appearance.PreviewFontSize, oldAppearance.PreviewFontSize, newAppearance.PreviewFontSize),
            },
            ImageInsertion = disk.ImageInsertion with
            {
                StorageMode = Select(disk.ImageInsertion.StorageMode, oldImages.StorageMode, newImages.StorageMode),
                LocalDirectory = Select(disk.ImageInsertion.LocalDirectory, oldImages.LocalDirectory, newImages.LocalDirectory),
                ProtectedImgBbApiKey = Select(disk.ImageInsertion.ProtectedImgBbApiKey, oldImages.ProtectedImgBbApiKey, newImages.ProtectedImgBbApiKey),
                TrustMode = policyChanged ? newImages.TrustMode : disk.ImageInsertion.TrustMode,
                RemoteImageRules = policyChanged ? newImages.RemoteImageRules : disk.ImageInsertion.RemoteImageRules,
            },
            RecentFiles = MergeRecentFiles(disk.RecentFiles, before.RecentFiles, after.RecentFiles),
            ShortcutOverrides = shortcutsChanged ? after.ShortcutOverrides : disk.ShortcutOverrides,
        };
    }

    private static T Select<T>(T persisted, T before, T after) => EqualityComparer<T>.Default.Equals(before, after) ? persisted : after;

    private static RecentFileEntry[] MergeRecentFiles(
        IReadOnlyList<RecentFileEntry> persisted, IReadOnlyList<RecentFileEntry> before, IReadOnlyList<RecentFileEntry> after)
    {
        Dictionary<string, RecentFileEntry> old = before.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        HashSet<string> retained = new(after.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);
        List<RecentFileEntry> merged = persisted.Where(entry => !old.ContainsKey(entry.Path) || retained.Contains(entry.Path)).ToList();
        // Reverse insertion preserves the requesting window's order for new items.
        foreach (RecentFileEntry entry in after.Reverse())
        {
            if (old.TryGetValue(entry.Path, out RecentFileEntry? previous) && previous == entry) continue;
            int index = merged.FindIndex(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, entry.Path));
            if (index < 0) merged.Insert(0, entry);
            else if (entry.LastOpenedUtc >= merged[index].LastOpenedUtc) merged[index] = entry;
        }
        return merged.Take(ApplicationSettings.MaximumRecentFiles).ToArray();
    }

}
