using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.Core.Tests.Editing;

public sealed class MarkdownFormattingInteractionTests
{
    [Theory]
    [InlineData("h1", "# ", 2)]
    [InlineData("h6", "###### ", 7)]
    [InlineData("unordered-list", "- ", 2)]
    [InlineData("ordered-list", "1. ", 3)]
    [InlineData("task-list", "- [ ] ", 6)]
    [InlineData("quote", "> ", 2)]
    public void Apply_BlockPrefixOnEmptyLine_CollapsesSelectionAfterMarker(
        string format,
        string expected,
        int expectedCaret)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(string.Empty, 0, 0, format);

        Assert.Equal(expected, edit.Text);
        Assert.Equal(expectedCaret, edit.SelectionStart);
        Assert.Equal(0, edit.SelectionLength);
        Assert.Equal(expectedCaret, edit.CaretOffset);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("separator")]
    public void Apply_InsertedBlock_LeavesCaretOnFollowingLine(string format)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(string.Empty, 0, 0, format);

        Assert.EndsWith("\n", edit.Text, StringComparison.Ordinal);
        Assert.Equal(0, edit.SelectionLength);
        Assert.Equal(edit.Text.Length, edit.CaretOffset);
    }

    [Theory]
    [InlineData("bold", "**内容**", 2)]
    [InlineData("italic", "*内容*", 1)]
    [InlineData("strike", "~~内容~~", 2)]
    public void Apply_AlreadyWrappedSelectedContent_RemovesFormatting(
        string format,
        string source,
        int contentStart)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(source, contentStart, 2, format);

        Assert.Equal("内容", edit.Text);
        Assert.Equal(0, edit.SelectionStart);
        Assert.Equal(2, edit.SelectionLength);
    }

    [Theory]
    [InlineData("bold", "**内容**")]
    [InlineData("italic", "*内容*")]
    [InlineData("strike", "~~内容~~")]
    public void Apply_SelectedFormattingAndContent_RemovesFormatting(
        string format,
        string source)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply(source, 0, source.Length, format);

        Assert.Equal("内容", edit.Text);
        Assert.Equal(0, edit.SelectionStart);
        Assert.Equal(2, edit.SelectionLength);
    }

    [Fact]
    public void Apply_ItalicToBoldSelection_PreservesBoldAndAddsItalic()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("**内容**", 2, 2, "italic");

        Assert.Equal("***内容***", edit.Text);
    }

    [Fact]
    public void Apply_HeadingReplacement_PreservesCaretInContentAndCollapsesSelection()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("## 标题", 5, 0, "h3");

        Assert.Equal("### 标题", edit.Text);
        Assert.Equal(6, edit.CaretOffset);
        Assert.Equal(0, edit.SelectionLength);
    }
}
