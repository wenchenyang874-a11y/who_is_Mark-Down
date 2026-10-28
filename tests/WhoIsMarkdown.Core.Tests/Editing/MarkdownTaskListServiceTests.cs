using AngleSharp.Html.Parser;
using WhoIsMarkdown.Core.Editing;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Editing;

public sealed class MarkdownTaskListServiceTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void 预览任务_引用嵌套引用与普通列表混排_行号映射且只替换状态字符(string newline)
    {
        string[] lines = ["# 任务", "", "- [ ] 普通任务", "> - [ ] 引用任务", "> - [X] 已完成引用任务",
            "> > 1. [ ] 嵌套引用任务", "1. [x] 有序任务", "   - [ ] 嵌套列表任务"];
        string source = string.Join(newline, lines);
        var html = new HtmlParser().ParseDocument(new MarkdownRenderer().RenderBody(source));
        var tasks = html.QuerySelectorAll("li.task-list-item");
        Assert.Equal(6, tasks.Length);
        foreach (var task in tasks)
        {
            string id = task.GetAttribute("id")!;
            Assert.StartsWith("pragma-line-", id, StringComparison.Ordinal);
            int line = int.Parse(id[12..], System.Globalization.CultureInfo.InvariantCulture);
            bool target = !task.QuerySelector("input")!.HasAttribute("checked");
            Assert.True(MarkdownTaskListService.TryCreateStateEdit(source, line, target, out var edit));
            Assert.NotNull(edit);
            Assert.True(edit.HasChanged);
            string changed = source[..edit.Offset] + edit.Replacement + source[(edit.Offset + 1)..];
            Assert.Equal(source.Length, changed.Length);
            Assert.Equal(1, source.Zip(changed).Count(pair => pair.First != pair.Second));
            int column = lines[line].IndexOf('[') + 1;
            Assert.Equal(lines.Take(line).Sum(value => value.Length + newline.Length) + column, edit.Offset);
            Assert.Equal(lines[line][..column] + edit.Replacement + lines[line][(column + 1)..], changed.Split(newline)[line]);
        }
    }

    [Theory]
    [InlineData("- [ ] 任务", 0, true, 3, 'x')]
    [InlineData("说明\n  * [X] 子任务", 1, false, 8, ' ')]
    [InlineData("1. [ ] 有序任务", 0, true, 4, 'x')]
    [InlineData("说明\r\n+ [x] 任务", 1, false, 7, ' ')]
    [InlineData("> - [ ] 引用任务", 0, true, 5, 'x')]
    [InlineData("> - [x] 引用任务", 0, false, 5, ' ')]
    [InlineData(">> - [X] 嵌套引用任务", 0, false, 6, ' ')]
    [InlineData("  > >  * [ ] 嵌套引用任务", 0, true, 10, 'x')]
    [InlineData("> 12. [ ] 有序引用任务", 0, true, 7, 'x')]
    [InlineData("说明\r\n>\t+ [ ] 引用任务", 1, true, 9, 'x')]
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
    [InlineData("> [ ] 没有列表标记", 0)]
    [InlineData("正文 > - [ ] 不是行首任务", 0)]
    [InlineData("> - [y] 非法状态", 0)]
    [InlineData("> ```- [ ] 不是任务", 0)]
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
