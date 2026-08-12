using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class RecentFileActionTargetsTests
{
    [Fact]
    public void Create_ChinesePathWithSpaces_ReturnsExactFileAndDirectoryTargets()
    {
        string expectedFile = System.IO.Path.GetFullPath(
            System.IO.Path.Combine("项目 文档", "使用指南.md"));

        RecentFileActionTargets targets = RecentFileActionTargets.Create(expectedFile);

        Assert.Equal(expectedFile, targets.FilePath);
        Assert.Equal(System.IO.Path.GetDirectoryName(expectedFile), targets.DirectoryPath);
    }

    [Fact]
    public void Create_RelativePath_NormalizesItBeforeShellOperations()
    {
        RecentFileActionTargets targets = RecentFileActionTargets.Create("notes.md");

        Assert.True(System.IO.Path.IsPathFullyQualified(targets.FilePath));
        Assert.Equal(System.IO.Path.GetDirectoryName(targets.FilePath), targets.DirectoryPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyPath_ThrowsArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => RecentFileActionTargets.Create(path));
    }
}
