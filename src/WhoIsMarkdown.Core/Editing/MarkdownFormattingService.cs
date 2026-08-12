using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Editing;

/// <summary>
/// Produces deterministic text edits for the Markdown toolbar. It contains no UI
/// dependencies, so toolbar buttons, menu commands, and future command palettes can
/// share the same behavior.
/// </summary>
public static partial class MarkdownFormattingService
{
    public static MarkdownTextEdit Apply(
        string text,
        int selectionStart,
        int selectionLength,
        string format)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ValidateSelection(text, selectionStart, selectionLength);

        return format switch
        {
            "bold" => Wrap(text, selectionStart, selectionLength, "**", "**", "粗体文本"),
            "italic" => Wrap(text, selectionStart, selectionLength, "*", "*", "斜体文本"),
            "strike" => Wrap(text, selectionStart, selectionLength, "~~", "~~", "删除线文本"),
            "inline-code" => Wrap(text, selectionStart, selectionLength, "`", "`", "代码"),
            "link" => Wrap(text, selectionStart, selectionLength, "[", "](https://)", "链接文字"),
            "image" => Wrap(text, selectionStart, selectionLength, "![", "](pic/image.png)", "图片说明"),
            "code-block" => Wrap(text, selectionStart, selectionLength, "```\n", "\n```", "代码"),
            "quote" => PrefixLines(text, selectionStart, selectionLength, "> "),
            "unordered-list" => PrefixLines(text, selectionStart, selectionLength, "- "),
            "ordered-list" => PrefixLines(text, selectionStart, selectionLength, "1. "),
            "task-list" => PrefixLines(text, selectionStart, selectionLength, "- [ ] "),
            "table" => InsertBlock(text, selectionStart, selectionLength, "| 列 1 | 列 2 |\n| --- | --- |\n| 内容 | 内容 |"),
            "separator" => InsertBlock(text, selectionStart, selectionLength, "---"),
            _ when HeadingFormatRegex().IsMatch(format) => ApplyHeading(
                text,
                selectionStart,
                selectionLength,
                int.Parse(format.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的 Markdown 格式。"),
        };
    }

    private static MarkdownTextEdit Wrap(
        string text,
        int start,
        int length,
        string prefix,
        string suffix,
        string placeholder)
    {
        string content = length > 0 ? text.Substring(start, length) : placeholder;
        string replacement = string.Concat(prefix, content, suffix);
        string result = text.Remove(start, length).Insert(start, replacement);
        int selectedStart = start + prefix.Length;
        return new MarkdownTextEdit(result, selectedStart, content.Length, selectedStart + content.Length);
    }

    private static MarkdownTextEdit InsertBlock(string text, int start, int length, string block)
    {
        string leadingBreak = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        string trailingBreak = start + length < text.Length && text[start + length] != '\n' ? "\n" : string.Empty;
        string replacement = string.Concat(leadingBreak, block, trailingBreak);
        string result = text.Remove(start, length).Insert(start, replacement);
        int blockStart = start + leadingBreak.Length;
        return new MarkdownTextEdit(result, blockStart, block.Length, blockStart + block.Length);
    }

    private static MarkdownTextEdit PrefixLines(string text, int start, int length, string prefix)
    {
        (int lineStart, int lineEnd) = FindSelectedLineRange(text, start, length);
        string selectedLines = text[lineStart..lineEnd];
        string replacement = prefix + selectedLines.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
        string result = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, replacement);
        return new MarkdownTextEdit(result, lineStart, replacement.Length, lineStart + replacement.Length);
    }

    private static MarkdownTextEdit ApplyHeading(string text, int start, int length, int level)
    {
        if (level is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        (int lineStart, int lineEnd) = FindSelectedLineRange(text, start, length);
        string selectedLines = text[lineStart..lineEnd];
        string prefix = new string('#', level) + " ";
        string replacement = string.Join(
            '\n',
            selectedLines.Split('\n').Select(line => prefix + ExistingHeadingRegex().Replace(line, string.Empty)));
        string result = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, replacement);
        return new MarkdownTextEdit(result, lineStart, replacement.Length, lineStart + prefix.Length);
    }

    private static (int Start, int End) FindSelectedLineRange(string text, int start, int length)
    {
        int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int selectedEnd = start + length;
        int lineEnd = text.IndexOf('\n', selectedEnd);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        return (lineStart, lineEnd);
    }

    private static void ValidateSelection(string text, int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start > text.Length || length > text.Length - start)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "选区超出文本范围。");
        }
    }

    [GeneratedRegex("^h[1-6]$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingFormatRegex();

    [GeneratedRegex("^#{1,6}\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ExistingHeadingRegex();
}
