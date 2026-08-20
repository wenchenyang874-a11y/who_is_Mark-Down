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
            CheckForUpdatesOnStartup = true,
            Appearance = new AppearanceSettings
            {
                Theme = ApplicationTheme.Warm,
                EditorFontFamily = "Consolas",
                EditorFontSize = 16,
                PreviewFontFamily = "Microsoft YaHei UI",
                PreviewFontSize = 18,
            },
            ImageInsertion = new ImageInsertionSettings
            {
                StorageMode = ImageStorageMode.ImgBb,
                LocalDirectory = "./assets/images/",
                TrustMode = RemoteImageTrustMode.AllowList,
                RemoteImageRules = ["domain:i.ibb.co", "keyword:public-image"],
                ProtectedImgBbApiKey = "ciphertext",
            },
            RecentFiles =
            [
                new(
                    documentPath,
                    new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero)),
            ],
            ShortcutOverrides = new Dictionary<string, ShortcutGesture>(StringComparer.Ordinal)
            {
                ["format.strike"] = new ShortcutGesture
                {
                    Key = "OemTilde",
                    Control = true,
                },
            },
        };

        store.Save(settings);
        ApplicationSettings result = store.Load();

        Assert.Equal(System.IO.Path.GetFullPath(documentPath), Assert.Single(result.RecentFiles).Path);
        Assert.Equal(System.IO.Path.GetFullPath(settings.BackgroundImagePath), result.BackgroundImagePath);
        Assert.Equal(0.32, result.BackgroundOpacity);
        Assert.False(result.IsRecentPaneExpanded);
        Assert.True(result.CheckForUpdatesOnStartup);
        Assert.Equal(ApplicationTheme.Warm, result.Appearance.Theme);
        Assert.Equal("Consolas", result.Appearance.EditorFontFamily);
        Assert.Equal(16, result.Appearance.EditorFontSize);
        Assert.Equal("Microsoft YaHei UI", result.Appearance.PreviewFontFamily);
        Assert.Equal(18, result.Appearance.PreviewFontSize);
        Assert.Equal(ImageStorageMode.ImgBb, result.ImageInsertion.StorageMode);
        Assert.Equal("./assets/images/", result.ImageInsertion.LocalDirectory);
        Assert.Equal(RemoteImageTrustMode.AllowList, result.ImageInsertion.TrustMode);
        Assert.Equal(["domain:i.ibb.co", "keyword:public-image"], result.ImageInsertion.RemoteImageRules);
        Assert.Equal("ciphertext", result.ImageInsertion.ProtectedImgBbApiKey);
        ShortcutGesture strike = Assert.Single(result.ShortcutOverrides).Value;
        Assert.Equal("Oem3", strike.Key);
        Assert.True(strike.Control);
        Assert.False(strike.Shift);
        Assert.Empty(Directory.EnumerateFiles(
            System.IO.Path.GetDirectoryName(settingsPath)!,
            ".settings.json.*.tmp"));
    }

    [Fact]
    public void Load_WhenShortcutOverridesAreMalformed_NormalizesWithoutBlockingStartup()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "ShortcutOverrides": {
                " format.bold ": { "Key": " b ", "Control": true },
                "format.italic": null,
                "format.strike": { "Key": " " }
              }
            }
            """);
        JsonApplicationSettingsStore store = new(settingsPath);

        ApplicationSettings result = store.Load();

        KeyValuePair<string, ShortcutGesture> shortcut = Assert.Single(result.ShortcutOverrides);
        Assert.Equal("format.bold", shortcut.Key);
        Assert.Equal("b", shortcut.Value.Key);
        Assert.True(shortcut.Value.Control);
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
