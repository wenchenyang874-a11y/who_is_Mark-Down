using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewResourceMappingStateTests
{
    [Fact]
    public void WouldChange_InspectingNewDirectory_DoesNotCommitMapping()
    {
        PreviewResourceMappingState state = new();
        string first = Path.Combine(Path.GetTempPath(), "first", "a.md");
        string second = Path.Combine(Path.GetTempPath(), "second", "b.md");
        state.Update(first);
        Assert.True(state.WouldChange(second));
        Assert.False(state.WouldChange(first));
        Assert.Equal(Path.GetDirectoryName(first), state.DirectoryPath);
        Assert.True(state.Update(second).HasChanged);
        Assert.False(state.WouldChange(second));
    }

    [Fact]
    public void 更新_从未命名文档切换到已保存文档_要求完整导航()
    {
        PreviewResourceMappingState state = new();
        PreviewResourceMappingUpdate initial = state.Update(documentPath: null);

        string documentPath = Path.Combine(Path.GetTempPath(), "WIMD", "README.md");
        PreviewResourceMappingUpdate opened = state.Update(documentPath);

        Assert.False(initial.HasChanged);
        Assert.True(opened.HasChanged);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(documentPath)), opened.DirectoryPath);
    }

    [Fact]
    public void 更新_连续打开同目录文件_保持增量更新()
    {
        PreviewResourceMappingState state = new();
        string directory = Path.Combine(Path.GetTempPath(), "WIMD");
        state.Update(Path.Combine(directory, "first.md"));

        PreviewResourceMappingUpdate update = state.Update(Path.Combine(directory, "second.md"));

        Assert.False(update.HasChanged);
    }

    [Fact]
    public void 更新_切换到其他目录_要求完整导航()
    {
        PreviewResourceMappingState state = new();
        state.Update(Path.Combine(Path.GetTempPath(), "WIMD", "first.md"));

        PreviewResourceMappingUpdate update = state.Update(
            Path.Combine(Path.GetTempPath(), "WIMD-Other", "second.md"));

        Assert.True(update.HasChanged);
    }
}
