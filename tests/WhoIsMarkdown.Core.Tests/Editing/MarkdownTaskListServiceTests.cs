using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.Core.Tests.Editing;

public sealed class MarkdownTaskListServiceTests
{
    [Theory]
    [InlineData("- [ ] 任务", 0, true, 3, 'x')]
    [InlineData("说明\n  * [X] 子任务", 1, false, 8, ' ')]
    [InlineData("1. [ ] 有序任务", 0, true, 4, 'x')]
    [InlineData("说明\r\n+ [x] 任务", 1, false, 7, ' ')]
    public void TryCreateStateEdit_TaskLine_ReturnsSingleMarkerEdit(
        string markdown,
        int line,
        bool completed,
        int expectedOffset,
        char expectedReplacement)
    {
        bool succeeded = MarkdownTaskListService.TryCreateStateEdit(
            markdown,
            line,
            completed,
            out MarkdownTaskStateEdit? edit);

        Assert.True(succeeded);
        Assert.NotNull(edit);
        Assert.Equal(expectedOffset, edit.Offset);
        Assert.Equal(expectedReplacement, edit.Replacement);
    }

    [Fact]
    public void TryCreateStateEdit_RequestedStateAlreadyMatches_ReportsNoTextChange()
    {
        bool succeeded = MarkdownTaskListService.TryCreateStateEdit(
            "- [x] 完成",
            0,
            isCompleted: true,
            out MarkdownTaskStateEdit? edit);

        Assert.True(succeeded);
        Assert.NotNull(edit);
        Assert.False(edit.HasChanged);
    }

    [Theory]
    [InlineData("普通文本", 0)]
    [InlineData("- [y] 非法状态", 0)]
    [InlineData("- [ ] 任务", -1)]
    [InlineData("- [ ] 任务", 1)]
    public void TryCreateStateEdit_InvalidOrStaleLine_RejectsRequest(string markdown, int line)
    {
        bool succeeded = MarkdownTaskListService.TryCreateStateEdit(
            markdown,
            line,
            isCompleted: true,
            out MarkdownTaskStateEdit? edit);

        Assert.False(succeeded);
        Assert.Null(edit);
    }
}
