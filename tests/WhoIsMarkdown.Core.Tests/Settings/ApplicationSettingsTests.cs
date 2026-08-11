using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ApplicationSettingsTests
{
    [Fact]
    public void RecordRecentFile_WhenPathAlreadyExists_MovesItToFrontWithoutDuplicates()
    {
        string firstPath = System.IO.Path.GetFullPath("first.md");
        string secondPath = System.IO.Path.GetFullPath("second.md");
        ApplicationSettings settings = new()
        {
            RecentFiles =
            [
                new(firstPath, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                new(secondPath, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        ApplicationSettings result = settings.RecordRecentFile(
            firstPath,
            new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal([firstPath, secondPath], result.RecentFiles.Select(entry => entry.Path));
        Assert.Equal(2, result.RecentFiles.Count);
    }

    [Fact]
    public void RemoveRecentFile_DoesNotDeleteTheReferencedDocument()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "保留.md");
        File.WriteAllText(documentPath, "content");
        ApplicationSettings settings = new()
        {
            RecentFiles = [new(documentPath, DateTimeOffset.UtcNow)],
        };

        ApplicationSettings result = settings.RemoveRecentFile(documentPath);

        Assert.Empty(result.RecentFiles);
        Assert.True(File.Exists(documentPath));
    }
}
