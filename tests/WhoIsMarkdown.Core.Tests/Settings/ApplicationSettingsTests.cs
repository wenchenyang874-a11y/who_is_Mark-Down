using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ApplicationSettingsTests
{
    [Fact]
    public void RecordRecentFile_WhenPathAlreadyExists_UpdatesTimestampWithoutReorderingSession()
    {
        string firstPath = System.IO.Path.GetFullPath("first.md");
        string secondPath = System.IO.Path.GetFullPath("second.md");
        DateTimeOffset reopenedAt = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        ApplicationSettings settings = new()
        {
            RecentFiles =
            [
                new(firstPath, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                new(secondPath, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ],
        };

        ApplicationSettings result = settings.RecordRecentFile(secondPath, reopenedAt);

        Assert.Equal([firstPath, secondPath], result.RecentFiles.Select(entry => entry.Path));
        Assert.Equal(2, result.RecentFiles.Count);
        Assert.Equal(reopenedAt, result.RecentFiles[1].LastOpenedUtc);
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
    [Fact]
    public void RelocateRecentFiles_WhenWorkspaceDirectoryIsRenamed_UpdatesNestedPaths()
    {
        string source = System.IO.Path.GetFullPath("旧目录");
        string target = System.IO.Path.GetFullPath("新目录");
        string documentPath = System.IO.Path.Combine(source, "子目录", "文档.md");
        ApplicationSettings settings = new()
        {
            RecentFiles = [new(documentPath, DateTimeOffset.UtcNow)],
        };

        ApplicationSettings result = settings.RelocateRecentFiles(source, target);

        Assert.Equal(
            System.IO.Path.Combine(target, "子目录", "文档.md"),
            Assert.Single(result.RecentFiles).Path);
    }

    [Fact]
    public void RemoveRecentFilesAtOrBelow_WhenDirectoryIsDeleted_RemovesOnlyNestedRecords()
    {
        string deletedDirectory = System.IO.Path.GetFullPath("待删除");
        string retainedPath = System.IO.Path.GetFullPath("保留.md");
        ApplicationSettings settings = new()
        {
            RecentFiles =
            [
                new(System.IO.Path.Combine(deletedDirectory, "文档.md"), DateTimeOffset.UtcNow),
                new(retainedPath, DateTimeOffset.UtcNow.AddMinutes(-1)),
            ],
        };

        ApplicationSettings result = settings.RemoveRecentFilesAtOrBelow(deletedDirectory);

        Assert.Equal(retainedPath, Assert.Single(result.RecentFiles).Path);
    }
}
