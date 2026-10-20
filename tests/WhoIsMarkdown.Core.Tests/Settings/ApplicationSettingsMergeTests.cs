using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ApplicationSettingsMergeTests
{
    [Fact]
    public async Task Load_DuringConcurrentAtomicReplacements_UsesSameTransactionLock()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore writer = new(path), reader = new(path);
        writer.Save(new());
        await Task.WhenAll(
            Task.Run(() =>
            {
                for (int index = 1; index <= 40; index++) writer.Save(new() { BackgroundOpacity = index / 50.0 });
            }, TestContext.Current.CancellationToken),
            Task.Run(() =>
            {
                for (int index = 0; index < 80; index++) Assert.InRange(reader.Load().BackgroundOpacity, 0, 1);
            }, TestContext.Current.CancellationToken));
        Assert.Equal(0.8, reader.Load().BackgroundOpacity);
    }

    [Fact]
    public void Merge_StaleWindow_PreservesUnchangedScalarsAndNestedFields()
    {
        ApplicationSettings initial = new();
        ApplicationSettings disk = initial with
        {
            BackgroundOpacity = 0.8,
            Appearance = initial.Appearance with { Theme = ApplicationTheme.Dark },
            ImageInsertion = initial.ImageInsertion with { LocalDirectory = "./assets/" }
        };
        ApplicationSettings local = initial with
        {
            IsRecentPaneExpanded = false,
            Appearance = initial.Appearance with { EditorFontSize = 18 },
            ImageInsertion = initial.ImageInsertion with { ProtectedImgBbApiKey = "encrypted" }
        };
        ApplicationSettings merged = ApplicationSettingsChanges.Merge(disk, initial, local);
        Assert.Equal(0.8, merged.BackgroundOpacity);
        Assert.False(merged.IsRecentPaneExpanded);
        Assert.Equal(ApplicationTheme.Dark, merged.Appearance.Theme);
        Assert.Equal(18, merged.Appearance.EditorFontSize);
        Assert.Equal("./assets/", merged.ImageInsertion.LocalDirectory);
        Assert.Equal("encrypted", merged.ImageInsertion.ProtectedImgBbApiKey);
    }

    [Fact]
    public void Merge_RecentAddRemoveUpdate_PreservesOtherWindowEntriesAndSessionOrder()
    {
        string a = Path.GetFullPath("a.md"), b = Path.GetFullPath("b.md"), c = Path.GetFullPath("c.md"), d = Path.GetFullPath("d.md");
        DateTimeOffset time = DateTimeOffset.UtcNow;
        ApplicationSettings before = new() { RecentFiles = [new(a, time), new(b, time)] };
        ApplicationSettings disk = before.RecordRecentFile(c, time.AddSeconds(1));
        ApplicationSettings local = before.RemoveRecentFile(a).RecordRecentFile(b, time.AddSeconds(2)).RecordRecentFile(d, time.AddSeconds(3));
        ApplicationSettings merged = ApplicationSettingsChanges.Merge(disk, before, local);
        Assert.Equal([d, c, b], merged.RecentFiles.Select(entry => entry.Path));
        Assert.Equal(time.AddSeconds(2), merged.RecentFiles[^1].LastOpenedUtc);
        ApplicationSettings staleUnrelated = before with { BackgroundOpacity = 0.4 };
        Assert.DoesNotContain(ApplicationSettingsChanges.Merge(merged, before, staleUnrelated).RecentFiles, entry => entry.Path == a);
    }

    [Fact]
    public void Merge_TrustAndShortcutConflicts_UsesWholeValidatedLocalGroup()
    {
        ApplicationSettings before = new();
        ApplicationSettings disk = before with
        {
            ImageInsertion = new() { TrustMode = RemoteImageTrustMode.AllowList, RemoteImageRules = ["domain:example.com"] },
            ShortcutOverrides = new Dictionary<string, ShortcutGesture> { ["bold"] = new() { Key = "Q", Control = true } }
        };
        ApplicationSettings local = before with
        {
            ImageInsertion = new() { TrustMode = RemoteImageTrustMode.BlockAll },
            ShortcutOverrides = new Dictionary<string, ShortcutGesture> { ["italic"] = new() { Key = "Q", Control = true } }
        };
        // Legacy trust groups normalize to the new image-compatible default;
        // shortcut groups still merge atomically, never per gesture.
        Assert.Equal(RemoteImageTrustMode.TrustAll, ApplicationSettingsChanges.Merge(disk, before, local).ImageInsertion.TrustMode);
        ApplicationSettings changed = local with { ImageInsertion = local.ImageInsertion with { TrustMode = RemoteImageTrustMode.TrustAll } };
        ApplicationSettings merged = ApplicationSettingsChanges.Merge(disk, before, changed);
        Assert.Equal(RemoteImageTrustMode.TrustAll, merged.ImageInsertion.TrustMode);
        Assert.Empty(merged.ImageInsertion.RemoteImageRules);
        Assert.Single(merged.ShortcutOverrides);
        Assert.True(merged.ShortcutOverrides.ContainsKey("italic"));
    }

    [Fact]
    public async Task SaveChanges_MultipleStoreInstances_MergesConcurrentChanges()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        ApplicationSettings initial = new();
        JsonApplicationSettingsStore first = new(path), second = new(path);
        first.Save(initial);
        await Task.WhenAll(Task.Run(() => first.SaveChanges(initial, initial with { BackgroundOpacity = 0.7 }), TestContext.Current.CancellationToken),
            Task.Run(() => second.SaveChanges(initial, initial with { IsRecentPaneExpanded = false }), TestContext.Current.CancellationToken));
        Assert.Equal(0.7, first.Load().BackgroundOpacity);
        Assert.False(first.Load().IsRecentPaneExpanded);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void SaveChanges_LockedDestination_PreservesOriginalAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore store = new(path);
        ApplicationSettings baseline = new() { BackgroundOpacity = 0.62 };
        store.Save(baseline);
        byte[] original = File.ReadAllBytes(path);
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<ApplicationSettingsStoreException>(() => store.SaveChanges(baseline, baseline with { BackgroundOpacity = 0.1 }));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
        store.SaveChanges(baseline, baseline with { BackgroundOpacity = 0.1 });
        Assert.Equal(0.1, store.Load().BackgroundOpacity);
    }

    [Fact]
    public void Merge_LateReopen_KeepsNewerExternalRecentTimestamp()
    {
        string path = Path.GetFullPath("recent.md");
        DateTimeOffset time = DateTimeOffset.UtcNow;
        ApplicationSettings baseline = new() { RecentFiles = [new(path, time)] };
        ApplicationSettings disk = baseline.RecordRecentFile(path, time.AddSeconds(2));
        ApplicationSettings late = baseline.RecordRecentFile(path, time.AddSeconds(1));
        Assert.Equal(time.AddSeconds(2), ApplicationSettingsChanges.Merge(disk, baseline, late).RecentFiles[0].LastOpenedUtc);
    }

    [Fact]
    public void Load_LegacyFallback_MigratesOnlyOnSuccessfulWrite()
    {
        using TemporaryDirectory directory = new();
        string oldPath = Path.Combine(directory.Path, "old.json"), newPath = Path.Combine(directory.Path, "new.json");
        JsonApplicationSettingsStore legacy = new(oldPath);
        legacy.Save(new() { BackgroundOpacity = 0.66 });
        JsonApplicationSettingsStore store = new(newPath, oldPath);
        ApplicationSettings initial = store.Load();
        Assert.Equal(0.66, initial.BackgroundOpacity);
        Assert.False(File.Exists(newPath));
        store.SaveChanges(initial, initial with { IsRecentPaneExpanded = false });
        Assert.True(File.Exists(oldPath));
        Assert.Equal(0.66, store.Load().BackgroundOpacity);
        Assert.False(store.Load().IsRecentPaneExpanded);
    }
}
