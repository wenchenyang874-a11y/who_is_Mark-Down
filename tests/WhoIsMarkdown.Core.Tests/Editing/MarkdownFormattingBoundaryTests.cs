using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.Core.Tests.Editing;

public sealed class MarkdownFormattingBoundaryTests
{
    [Theory]
    [InlineData("italic", "*内容*")]
    [InlineData("strike", "~~内容~~")]
    [InlineData("inline-code", "`内容`")]
    [InlineData("link", "[内容](https://)")]
    [InlineData("code-block", "```\n内容\n```")]
    [InlineData("quote", "> 内容")]
    [InlineData("unordered-list", "- 内容")]
    [InlineData("ordered-list", "1. 内容")]
    [InlineData("table", "| 列 1 | 列 2 |\n| --- | --- |\n| 内容 | 内容 |\n")]
    [InlineData("separator", "---\n\n")]
    public void Apply_SupportedToolbarFormat_ProducesExpectedMarkdown(
        string format,
        string expected)
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("内容", 0, 2, format);

        Assert.Equal(expected, edit.Text);
    }

    [Fact]
    public void Apply_BlockInsideText_AddsRequiredLineBreaks()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("前后", 1, 0, "separator");

        Assert.Equal("前\n\n---\n\n后", edit.Text);
    }

    [Fact]
    public void Apply_SeparatorBesideExistingBlankLines_DoesNotDuplicateBlankLines()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("前\n\n后", 2, 0, "separator");

        Assert.Equal("前\n\n---\n\n后", edit.Text);
    }

    [Fact]
    public void Apply_SeparatorInCrLfDocument_PreservesLineEndingStyle()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("前\r\n后", 3, 0, "separator");

        Assert.Equal("前\r\n\r\n---\r\n\r\n后", edit.Text);
    }

    [Fact]
    public void ApplyTable_SelectedDimensions_GeneratesRequestedRowsAndColumns()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.ApplyTable(string.Empty, 0, 0, 4, 3);

        Assert.Equal(
            "| 列 1 | 列 2 | 列 3 |\n" +
            "| --- | --- | --- |\n" +
            "| 内容 | 内容 | 内容 |\n" +
            "| 内容 | 内容 | 内容 |\n" +
            "| 内容 | 内容 | 内容 |\n",
            edit.Text);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(21, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 13)]
    public void ApplyTable_UnsupportedDimensions_Throws(int rows, int columns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownFormattingService.ApplyTable(string.Empty, 0, 0, rows, columns));
    }

    [Fact]
    public void Apply_PrefixInsideMultilineText_OnlyChangesSelectedLine()
    {
        MarkdownTextEdit edit = MarkdownFormattingService.Apply("头\n中\n尾", 2, 1, "quote");

        Assert.Equal("头\n> 中\n尾", edit.Text);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    public void Apply_InvalidSelection_Throws(int start, int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MarkdownFormattingService.Apply("内容", start, length, "bold"));
    }
}
