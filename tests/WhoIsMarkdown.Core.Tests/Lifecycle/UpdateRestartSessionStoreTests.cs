using WhoIsMarkdown.Core.Lifecycle;

namespace WhoIsMarkdown.Core.Tests.Lifecycle;

public sealed class UpdateRestartSessionStoreTests
{
    [Fact]
    public void SaveAndConsume_WhenInstallerRequestExists_RoundTripsMultipleWindowStates()
    {
        using TemporaryDirectory temporaryDirectory = new();
        UpdateRestartSessionStore store = new(temporaryDirectory.Path);
        Directory.CreateDirectory(temporaryDirectory.Path);
        const string token = "20260826093000-123456";
        File.WriteAllText(store.RequestFilePath, $"capture:{token}");
        UpdateRestartWindowState first = CreateState("first.md", "PreviewOnly", 120);
        UpdateRestartWindowState second = CreateState("second.md", "EditorOnly", 240);

        bool firstSaved = store.TrySaveRequestedWindow(Guid.NewGuid().ToString("N"), first);
        bool secondSaved = store.TrySaveRequestedWindow(Guid.NewGuid().ToString("N"), second);
        File.WriteAllText(store.RequestFilePath, $"restore:{token}");
        IReadOnlyList<UpdateRestartWindowState> restored = store.ConsumeRequestedWindows();

        Assert.True(firstSaved);
        Assert.True(secondSaved);
        Assert.Equal(2, restored.Count);
        Assert.Contains(restored, state => state.DocumentPath!.EndsWith("first.md", StringComparison.Ordinal));
        Assert.Contains(restored, state => state.DocumentPath!.EndsWith("second.md", StringComparison.Ordinal));
        Assert.Contains(restored, state => state.DocumentText == "unsaved first.md");
        Assert.Contains(restored, state => state.SavedDocumentText == "saved second.md");
        Assert.False(File.Exists(store.RequestFilePath));
        Assert.False(Directory.Exists(Path.Combine(
            temporaryDirectory.Path,
            UpdateRestartSessionStore.SessionDirectoryName,
            token)));
    }

    [Fact]
    public void Save_WhenNoFreshInstallerRequestExists_DoesNotCreateSnapshot()
    {
        using TemporaryDirectory temporaryDirectory = new();
        UpdateRestartSessionStore store = new(temporaryDirectory.Path);

        bool result = store.TrySaveRequestedWindow(
            Guid.NewGuid().ToString("N"),
            CreateState("document.md", "EditorAndPreview", 0));

        Assert.False(result);
        Assert.False(Directory.Exists(Path.Combine(
            temporaryDirectory.Path,
            UpdateRestartSessionStore.SessionDirectoryName)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("too short")]
    [InlineData("invalid/token")]
    public void Consume_WhenRequestTokenIsInvalid_RejectsTraversalAndDeletesMarker(string token)
    {
        using TemporaryDirectory temporaryDirectory = new();
        UpdateRestartSessionStore store = new(temporaryDirectory.Path);
        Directory.CreateDirectory(temporaryDirectory.Path);
        File.WriteAllText(store.RequestFilePath, token);

        IReadOnlyList<UpdateRestartWindowState> restored = store.ConsumeRequestedWindows();

        Assert.Empty(restored);
        Assert.False(File.Exists(store.RequestFilePath));
    }

    [Fact]
    public void Consume_WhenRequestIsStillCapturing_DoesNotConsumeSnapshots()
    {
        using TemporaryDirectory temporaryDirectory = new();
        UpdateRestartSessionStore store = new(temporaryDirectory.Path);
        Directory.CreateDirectory(temporaryDirectory.Path);
        File.WriteAllText(store.RequestFilePath, "capture:20260826093000-123456");
        Assert.True(store.TrySaveRequestedWindow(
            Guid.NewGuid().ToString("N"),
            CreateState("document.md", "EditorAndPreview", 10)));

        IReadOnlyList<UpdateRestartWindowState> restored = store.ConsumeRequestedWindows();

        Assert.Empty(restored);
        Assert.True(File.Exists(store.RequestFilePath));
    }

    [Fact]
    public void Normalize_WhenWindowValuesAreInvalid_UsesSafeFallbacks()
    {
        UpdateRestartWindowState result = CreateState("document.md", "Unknown", -10) with
        {
            Width = double.NaN,
            Height = 300,
            EditorVerticalOffset = double.PositiveInfinity,
            UntitledDisplayName = "\0",
        };

        result = result.Normalize();

        Assert.Equal("EditorAndPreview", result.ViewMode);
        Assert.Equal(1320, result.Width);
        Assert.Equal(600, result.Height);
        Assert.Equal(0, result.CaretOffset);
        Assert.Equal(0, result.EditorVerticalOffset);
        Assert.Equal("未命名-1", result.UntitledDisplayName);
    }

    private static UpdateRestartWindowState CreateState(
        string documentName,
        string viewMode,
        int caretOffset)
    {
        return new UpdateRestartWindowState
        {
            WorkspacePath = Path.Combine(Path.GetTempPath(), "WIMD", "workspace"),
            DocumentPath = Path.Combine(Path.GetTempPath(), "WIMD", documentName),
            DocumentText = $"unsaved {documentName}",
            SavedDocumentText = $"saved {documentName}",
            ViewMode = viewMode,
            Left = 30,
            Top = 40,
            Width = 1200,
            Height = 760,
            IsMaximized = true,
            CaretOffset = caretOffset,
            EditorVerticalOffset = 180,
            SavedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}
