using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class JsonApplicationSettingsStoreBoundaryTests
{
    [Fact]
    public void Load_WhenSettingsFileDoesNotExist_ReturnsDefaults()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "missing", "settings.json");
        JsonApplicationSettingsStore store = new(path);

        ApplicationSettings result = store.Load();

        Assert.Empty(result.RecentFiles);
        Assert.Equal(ApplicationSettings.DefaultBackgroundOpacity, result.BackgroundOpacity);
    }

    [Fact]
    public void Load_WhenJsonValueIsNull_ReturnsDefaults()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        File.WriteAllText(path, "null");
        JsonApplicationSettingsStore store = new(path);

        ApplicationSettings result = store.Load();

        Assert.Empty(result.RecentFiles);
        Assert.True(result.IsRecentPaneExpanded);
    }

    [Fact]
    public void Load_WhenUtf8IsInvalid_ReturnsDefaults()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        File.WriteAllBytes(path, [0xC3, 0x28]);
        JsonApplicationSettingsStore store = new(path);

        ApplicationSettings result = store.Load();

        Assert.Empty(result.RecentFiles);
    }
}
