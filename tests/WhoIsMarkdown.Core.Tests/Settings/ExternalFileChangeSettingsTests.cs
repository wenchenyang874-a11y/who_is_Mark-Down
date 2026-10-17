using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ExternalFileChangeSettingsTests
{
    [Fact]
    public void Resolve_DefaultAndOverride_OnlyExactFileOverridesGlobal()
    {
        string path = Path.GetFullPath("目录/文件.md");
        ExternalFileChangeSettings settings = new() { DefaultBehavior = ExternalFileChangeBehavior.AutoReload };
        settings = settings.WithOverride(path, ExternalFileChangeBehavior.Prompt);
        Assert.Equal(ExternalFileChangeBehavior.Prompt, settings.Resolve(path.ToUpperInvariant()));
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, settings.Resolve(Path.GetFullPath("其他/文件.md")));
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, settings.Resolve(null));
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, settings.WithOverride(path, null).Resolve(path));
        Assert.Empty(settings.WithOverride(path, null).FileOverrides);
    }

    [Fact]
    public void Normalize_InvalidOrMissingValues_FallsBackWithoutProbingDisk()
    {
        ExternalFileChangeSettings settings = new()
        {
            DefaultBehavior = (ExternalFileChangeBehavior)999,
            FileOverrides = new Dictionary<string, ExternalFileChangeBehavior>
            {
                [" "] = ExternalFileChangeBehavior.AutoReload,
                ["bad\0.md"] = ExternalFileChangeBehavior.AutoReload,
                ["bad-mode.md"] = (ExternalFileChangeBehavior)999,
                ["missing/文件.md"] = ExternalFileChangeBehavior.AutoReload,
            },
        };
        ExternalFileChangeSettings normalized = settings.Normalize();
        Assert.Equal(ExternalFileChangeBehavior.Prompt, normalized.DefaultBehavior);
        Assert.Single(normalized.FileOverrides);
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, normalized.Resolve("missing/文件.md"));
        Assert.Empty((settings with { FileOverrides = null! }).Normalize().FileOverrides);
        Assert.Equal(ExternalFileChangeBehavior.Prompt, new ApplicationSettings { ExternalFileChanges = null! }.Normalize().ExternalFileChanges.DefaultBehavior);
        Assert.Throws<ArgumentException>(() => settings.WithOverride(" ", null));
    }

    [Fact]
    public void Capture_MutableDictionary_DetachesPendingSnapshot()
    {
        Dictionary<string, ExternalFileChangeBehavior> overrides = new() { ["a.md"] = ExternalFileChangeBehavior.AutoReload };
        ApplicationSettings captured = ApplicationSettingsChanges.Capture(new() { ExternalFileChanges = new() { FileOverrides = overrides } });
        overrides.Clear();
        Assert.Single(captured.ExternalFileChanges.FileOverrides);
    }

    [Fact]
    public void Merge_DifferentFilesAndDefault_PreservesOtherWindowChanges()
    {
        ApplicationSettings baseline = new();
        ApplicationSettings disk = baseline with
        {
            ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("a.md", ExternalFileChangeBehavior.Prompt)
                with
            { DefaultBehavior = ExternalFileChangeBehavior.AutoReload },
        };
        ApplicationSettings local = baseline with { ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("b.md", ExternalFileChangeBehavior.AutoReload) };
        ApplicationSettings merged = ApplicationSettingsChanges.Merge(disk, baseline, local);
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, merged.ExternalFileChanges.DefaultBehavior);
        Assert.Equal(2, merged.ExternalFileChanges.FileOverrides.Count);
        Assert.Equal(ExternalFileChangeBehavior.Prompt, merged.ExternalFileChanges.Resolve("a.md"));
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, merged.ExternalFileChanges.Resolve("b.md"));
    }

    [Fact]
    public void Merge_SameFileAndReset_LastExplicitChangeWinsWithoutResurrectingRemovedEntry()
    {
        ApplicationSettings baseline = new() { ExternalFileChanges = new ExternalFileChangeSettings().WithOverride("a.md", ExternalFileChangeBehavior.AutoReload) };
        ApplicationSettings disk = baseline with { ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("a.md", ExternalFileChangeBehavior.Prompt) };
        ApplicationSettings reset = baseline with { ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("a.md", null) };
        Assert.Null(ApplicationSettingsChanges.Merge(disk, baseline, reset).ExternalFileChanges.GetOverride("a.md"));
        ApplicationSettings afterReset = ApplicationSettingsChanges.Merge(reset, baseline, baseline with { BackgroundOpacity = 0.7 });
        Assert.Null(afterReset.ExternalFileChanges.GetOverride("a.md"));
        ApplicationSettings changed = baseline with { ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("a.md", ExternalFileChangeBehavior.Prompt) };
        Assert.Equal(ExternalFileChangeBehavior.Prompt, ApplicationSettingsChanges.Merge(reset, baseline, changed).ExternalFileChanges.GetOverride("a.md"));
    }

    [Fact]
    public void Store_LegacyAndNewSettings_RoundTripsPathChoices()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"IsRecentPaneExpanded\":false}");
        JsonApplicationSettingsStore store = new(path);
        ApplicationSettings old = store.Load();
        Assert.False(old.IsRecentPaneExpanded);
        Assert.Equal(ExternalFileChangeBehavior.Prompt, old.ExternalFileChanges.Resolve("a.md"));
        ApplicationSettings next = old with { ExternalFileChanges = old.ExternalFileChanges.WithOverride("中文 空格/文件.md", ExternalFileChangeBehavior.AutoReload) };
        store.SaveChanges(old, next);
        Assert.Equal(ExternalFileChangeBehavior.AutoReload, store.Load().ExternalFileChanges.Resolve("中文 空格/文件.md"));
        ApplicationSettings reset = next with { ExternalFileChanges = next.ExternalFileChanges.WithOverride("中文 空格/文件.md", null) };
        store.SaveChanges(next, reset);
        Assert.Empty(store.Load().ExternalFileChanges.FileOverrides);
    }

    [Fact]
    public async Task SaveChanges_TwoWindowsUpdateDifferentFileChoices_BothPersist()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore first = new(path), second = new(path);
        ApplicationSettings baseline = new();
        first.Save(baseline);
        await Task.WhenAll(
            Task.Run(() => first.SaveChanges(baseline, baseline with
            {
                ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("first.md", ExternalFileChangeBehavior.Prompt),
            }), TestContext.Current.CancellationToken),
            Task.Run(() => second.SaveChanges(baseline, baseline with
            {
                ExternalFileChanges = baseline.ExternalFileChanges.WithOverride("second.md", ExternalFileChangeBehavior.AutoReload),
            }), TestContext.Current.CancellationToken));
        Assert.Equal(2, first.Load().ExternalFileChanges.FileOverrides.Count);
    }
}
