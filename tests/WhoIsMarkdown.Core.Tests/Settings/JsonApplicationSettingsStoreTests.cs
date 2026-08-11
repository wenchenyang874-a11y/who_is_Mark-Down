using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class JsonApplicationSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_WhenSettingsContainChinesePaths_RoundTripsNormalizedValues()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "设置", "settings.json");
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "中文文档.md");
        JsonApplicationSettingsStore store = new(settingsPath);
        ApplicationSettings settings = new()
        {
            BackgroundImagePath = System.IO.Path.Combine(temporaryDirectory.Path, "背景.png"),
            BackgroundOpacity = 0.32,
            IsRecentPaneExpanded = false,
            RecentFiles =
            [
                new(
                    documentPath,
                    new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero)),
            ],
        };

        store.Save(settings);
        ApplicationSettings result = store.Load();

        Assert.Equal(System.IO.Path.GetFullPath(documentPath), Assert.Single(result.RecentFiles).Path);
        Assert.Equal(System.IO.Path.GetFullPath(settings.BackgroundImagePath), result.BackgroundImagePath);
        Assert.Equal(0.32, result.BackgroundOpacity);
        Assert.False(result.IsRecentPaneExpanded);
        Assert.Empty(Directory.EnumerateFiles(
            System.IO.Path.GetDirectoryName(settingsPath)!,
            ".settings.json.*.tmp"));
    }

    [Fact]
    public void Load_WhenJsonIsMalformed_ReturnsDefaultsWithoutBlockingStartup()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        File.WriteAllText(settingsPath, "{not-json");
        JsonApplicationSettingsStore store = new(settingsPath);

        ApplicationSettings result = store.Load();

        Assert.Empty(result.RecentFiles);
        Assert.Null(result.BackgroundImagePath);
        Assert.Equal(ApplicationSettings.DefaultBackgroundOpacity, result.BackgroundOpacity);
    }
}
