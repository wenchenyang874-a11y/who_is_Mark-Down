using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.Core.Tests.Editing;

public sealed class MarkdownFormattingServiceTests
{
    [Fact]
    public void Apply_BoldSelection_WrapsAndKeepsContentSelected()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("示例文本", 0, 2, "bold");

        Assert.Equal("**示例**文本", edit.Text);
        Assert.Equal(2, edit.SelectionStart);
        Assert.Equal(2, edit.SelectionLength);
    }

    [Fact]
    public void Apply_Heading_ReplacesExistingHeadingMarker()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("## 原标题\n正文", 3, 0, "h3");

        Assert.Equal("### 原标题\n正文", edit.Text);
    }

    [Fact]
    public void Apply_TaskList_PrefixesEverySelectedLine()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("第一项\n第二项", 0, 7, "task-list");

        Assert.Equal("- [ ] 第一项\n- [ ] 第二项", edit.Text);
    }

    [Fact]
    public void Apply_ImageWithoutSelection_InsertsEditablePlaceholder()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(string.Empty, 0, 0, "image");

        Assert.Equal("![图片说明](pic/image.png)", edit.Text);
        Assert.Equal("图片说明", edit.Text.Substring(edit.SelectionStart, edit.SelectionLength));
    }

    [Fact]
    public void Apply_UnknownFormat_ThrowsContextualError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownFormattingService.Apply(string.Empty, 0, 0, "unknown"));
    }
}
