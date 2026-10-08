using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class FileOpenViewModeTests
{
    [Fact]
    public void Defaults_RememberLast_InitiallySplit()
    {
        ApplicationSettings settings = new();
        Assert.Equal(FileOpenViewMode.RememberLast, settings.FileOpenViewMode);
        Assert.Equal(WorkspaceViewMode.EditorAndPreview, settings.ResolveFileOpenViewMode());
    }

    [Theory]
    [InlineData(FileOpenViewMode.EditorOnly, WorkspaceViewMode.EditorOnly)]
    [InlineData(FileOpenViewMode.EditorAndPreview, WorkspaceViewMode.EditorAndPreview)]
    [InlineData(FileOpenViewMode.PreviewOnly, WorkspaceViewMode.PreviewOnly)]
    public void FixedPolicy_IgnoresAllLastLayouts(FileOpenViewMode policy, WorkspaceViewMode expected)
    {
        foreach (WorkspaceViewMode last in Enum.GetValues<WorkspaceViewMode>())
        {
            ApplicationSettings settings = new() { FileOpenViewMode = policy, LastViewMode = last };
            Assert.Equal(expected, settings.ResolveFileOpenViewMode());
        }
    }

    [Theory]
    [InlineData(WorkspaceViewMode.EditorOnly)]
    [InlineData(WorkspaceViewMode.EditorAndPreview)]
    [InlineData(WorkspaceViewMode.PreviewOnly)]
    public void RememberLast_UsesPersistedLayoutAfterRestart(WorkspaceViewMode last)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore store = new(path);
        store.Save(new ApplicationSettings { LastViewMode = last });

        ApplicationSettings reloaded = new JsonApplicationSettingsStore(path).Load();
        Assert.Equal(FileOpenViewMode.RememberLast, reloaded.FileOpenViewMode);
        Assert.Equal(last, reloaded.ResolveFileOpenViewMode());
    }

    [Theory]
    [InlineData(FileOpenViewMode.EditorOnly)]
    [InlineData(FileOpenViewMode.EditorAndPreview)]
    [InlineData(FileOpenViewMode.PreviewOnly)]
    [InlineData(FileOpenViewMode.RememberLast)]
    public void Store_PreservesPolicyAndLastLayoutSeparately(FileOpenViewMode policy)
    {
        using TemporaryDirectory directory = new();
        JsonApplicationSettingsStore store = new(Path.Combine(directory.Path, "settings.json"));
        store.Save(new ApplicationSettings { FileOpenViewMode = policy, LastViewMode = WorkspaceViewMode.EditorOnly });
        ApplicationSettings loaded = store.Load();
        Assert.Equal(policy, loaded.FileOpenViewMode);
        Assert.Equal(WorkspaceViewMode.EditorOnly, loaded.LastViewMode);
    }

    [Fact]
    public void LegacySettings_WithoutViewFields_KeepSplitDefault()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"IsRecentPaneExpanded\":false}");
        ApplicationSettings loaded = new JsonApplicationSettingsStore(path).Load();
        Assert.False(loaded.IsRecentPaneExpanded);
        Assert.Equal(FileOpenViewMode.RememberLast, loaded.FileOpenViewMode);
        Assert.Equal(WorkspaceViewMode.EditorAndPreview, loaded.ResolveFileOpenViewMode());
    }

    [Fact]
    public void InvalidEnumValues_NormalizeAndResolveSafely()
    {
        ApplicationSettings invalid = new() { FileOpenViewMode = (FileOpenViewMode)999, LastViewMode = (WorkspaceViewMode)(-1) };
        Assert.Equal(WorkspaceViewMode.EditorAndPreview, invalid.ResolveFileOpenViewMode());
        ApplicationSettings normalized = invalid.Normalize();
        Assert.Equal(FileOpenViewMode.RememberLast, normalized.FileOpenViewMode);
        Assert.Equal(WorkspaceViewMode.EditorAndPreview, normalized.LastViewMode);
    }
}
