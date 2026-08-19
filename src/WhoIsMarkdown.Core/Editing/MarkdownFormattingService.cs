using System.Globalization;
using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Editing;

/// <summary>
/// Produces deterministic text edits for the Markdown toolbar. It contains no UI
/// dependencies, so toolbar buttons, menu commands, and future command palettes can
/// share the same behavior.
/// </summary>
public static partial class MarkdownFormattingService
{
    public const int MinimumTableRowCount = 2;

    public const int MaximumTableRowCount = 20;

    public const int MinimumTableColumnCount = 1;

    public const int MaximumTableColumnCount = 12;

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
            "bold" => ToggleWrap(text, selectionStart, selectionLength, "**", "**", "粗体文本"),
            "italic" => ToggleWrap(text, selectionStart, selectionLength, "*", "*", "斜体文本"),
            "strike" => ToggleWrap(text, selectionStart, selectionLength, "~~", "~~", "删除线文本"),
            "inline-code" => ToggleWrap(text, selectionStart, selectionLength, "`", "`", "代码"),
            "link" => Wrap(text, selectionStart, selectionLength, "[", "](https://)", "链接文字"),
            "image" => Wrap(text, selectionStart, selectionLength, "![", "](pic/image.png)", "图片说明"),
            "code-block" => Wrap(text, selectionStart, selectionLength, "```\n", "\n```", "代码"),
            "quote" => PrefixLines(text, selectionStart, selectionLength, "> "),
            "unordered-list" => PrefixLines(text, selectionStart, selectionLength, "- "),
            "ordered-list" => PrefixLines(text, selectionStart, selectionLength, "1. "),
            "task-list" => PrefixLines(text, selectionStart, selectionLength, "- [ ] "),
            "table" => ApplyTable(text, selectionStart, selectionLength, 2, 2),
            "separator" => InsertStandaloneSeparator(text, selectionStart, selectionLength),
            _ when HeadingFormatRegex().IsMatch(format) => ApplyHeading(
                text,
                selectionStart,
                selectionLength,
                int.Parse(format.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知的 Markdown 格式。"),
        };
    }

    /// <summary>
    /// Creates a pipe table with a header included in <paramref name="rowCount"/>.
    /// Explicit limits keep accidental toolbar input from generating an enormous
    /// editor replacement while still covering practical Markdown tables.
    /// </summary>
    public static MarkdownTextEdit ApplyTable(
        string text,
        int selectionStart,
        int selectionLength,
        int rowCount,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateSelection(text, selectionStart, selectionLength);
        if (rowCount is < MinimumTableRowCount or > MaximumTableRowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        }

        if (columnCount is < MinimumTableColumnCount or > MaximumTableColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(columnCount));
        }

        string header = CreateTableRow(
            Enumerable.Range(1, columnCount)
                .Select(index => $"列 {index.ToString(CultureInfo.InvariantCulture)}"));
        string delimiter = CreateTableRow(Enumerable.Repeat("---", columnCount));
        string content = CreateTableRow(Enumerable.Repeat("内容", columnCount));
        string table = string.Join('\n', new[] { header, delimiter }
            .Concat(Enumerable.Repeat(content, rowCount - 1)));
        return InsertBlock(text, selectionStart, selectionLength, table);
    }

    /// <summary>
    /// Toggle behavior keeps the content selected so pressing the same shortcut a
    /// second time removes the markers. Markers may either surround the selection
    /// or be included in it, matching how users commonly select formatted text.
    /// </summary>
    private static MarkdownTextEdit ToggleWrap(
        string text,
        int start,
        int length,
        string prefix,
        string suffix,
        string placeholder)
    {
        if (length > 0
            && IsExactWrappedSelection(text, start, length, prefix, suffix))
        {
            int contentLength = length - prefix.Length - suffix.Length;
            string content = text.Substring(start + prefix.Length, contentLength);
            string result = text.Remove(start, length).Insert(start, content);
            return new MarkdownTextEdit(result, start, contentLength, start + contentLength);
        }

        if (length > 0
            && IsSelectionSurrounded(text, start, length, prefix, suffix))
        {
            string result = text
                .Remove(start + length, suffix.Length)
                .Remove(start - prefix.Length, prefix.Length);
            int selectedStart = start - prefix.Length;
            return new MarkdownTextEdit(result, selectedStart, length, selectedStart + length);
        }

        return Wrap(text, start, length, prefix, suffix, placeholder);
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

    /// <summary>
    /// Block templates always finish on a new line and collapse the selection there.
    /// This prevents the first typed character from replacing the generated table or
    /// separator and gives the user a valid place to continue writing.
    /// </summary>
    private static MarkdownTextEdit InsertBlock(string text, int start, int length, string block)
    {
        string leadingBreak = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        int originalEnd = start + length;
        bool followedByLineBreak = originalEnd < text.Length && text[originalEnd] == '\n';
        string trailingBreak = followedByLineBreak ? string.Empty : "\n";
        string replacement = string.Concat(leadingBreak, block, trailingBreak);
        string result = text.Remove(start, length).Insert(start, replacement);
        int caretOffset = start + replacement.Length + (followedByLineBreak ? 1 : 0);
        return new MarkdownTextEdit(result, caretOffset, 0, caretOffset);
    }

    /// <summary>
    /// Bug fix: a thematic break directly adjacent to text can be parsed as a
    /// heading underline or merge with the next block. Keep one empty line on both
    /// sides whenever surrounding content exists, and leave an empty line ready for
    /// continued typing at the end of the document.
    /// </summary>
    private static MarkdownTextEdit InsertStandaloneSeparator(string text, int start, int length)
    {
        string prefix = text[..start];
        string suffix = text[(start + length)..];
        string lineBreak = DetectLineBreak(text);
        int leadingBreakCount = CountTrailingLineBreaks(prefix);
        int trailingBreakCount = CountLeadingLineBreaks(suffix);
        string leadingBreaks = prefix.Length == 0
            ? string.Empty
            : RepeatLineBreak(lineBreak, Math.Max(0, 2 - leadingBreakCount));
        string trailingBreaks = RepeatLineBreak(lineBreak, Math.Max(0, 2 - trailingBreakCount));
        string replacement = string.Concat(leadingBreaks, "---", trailingBreaks);
        string result = text.Remove(start, length).Insert(start, replacement);
        int caretOffset = start + replacement.Length;
        return new MarkdownTextEdit(result, caretOffset, 0, caretOffset);
    }

    private static string CreateTableRow(IEnumerable<string> cells)
    {
        return $"| {string.Join(" | ", cells)} |";
    }

    private static string DetectLineBreak(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static int CountTrailingLineBreaks(string value)
    {
        int count = 0;
        for (int index = value.Length - 1; index >= 0 && value[index] == '\n'; index--)
        {
            count++;
            if (index > 0 && value[index - 1] == '\r')
            {
                index--;
            }
        }

        return count;
    }

    private static int CountLeadingLineBreaks(string value)
    {
        int count = 0;
        int index = 0;
        while (index < value.Length)
        {
            if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                count++;
                index += 2;
                continue;
            }

            if (value[index] != '\n')
            {
                break;
            }

            count++;
            index++;
        }

        return count;
    }

    private static string RepeatLineBreak(string lineBreak, int count)
    {
        return string.Concat(Enumerable.Repeat(lineBreak, count));
    }

    private static MarkdownTextEdit PrefixLines(string text, int start, int length, string prefix)
    {
        (int lineStart, int lineEnd) = FindSelectedLineRange(text, start, length);
        string selectedLines = text[lineStart..lineEnd];
        string replacement = prefix + selectedLines.Replace("\n", "\n" + prefix, StringComparison.Ordinal);
        string result = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, replacement);
        int caretOffset = length == 0
            ? Math.Min(start + prefix.Length, lineStart + replacement.Length)
            : lineStart + replacement.Length;
        return new MarkdownTextEdit(result, caretOffset, 0, caretOffset);
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
        string[] originalLines = selectedLines.Split('\n');
        string replacement = string.Join(
            '\n',
            originalLines.Select(line => prefix + ExistingHeadingRegex().Replace(line, string.Empty)));
        string result = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, replacement);

        int caretOffset;
        if (length == 0)
        {
            Match existingMarker = ExistingHeadingRegex().Match(originalLines[0]);
            int relativeCaret = start - lineStart;
            int contentOffset = Math.Max(0, relativeCaret - existingMarker.Length);
            caretOffset = Math.Min(
                lineStart + prefix.Length + contentOffset,
                lineStart + replacement.Length);
        }
        else
        {
            caretOffset = lineStart + replacement.Length;
        }

        return new MarkdownTextEdit(result, caretOffset, 0, caretOffset);
    }

    private static bool IsExactWrappedSelection(
        string text,
        int start,
        int length,
        string prefix,
        string suffix)
    {
        if (length < prefix.Length + suffix.Length)
        {
            return false;
        }

        ReadOnlySpan<char> selection = text.AsSpan(start, length);
        if (!selection.StartsWith(prefix, StringComparison.Ordinal)
            || !selection.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        // A bold marker begins with '*', but selecting **text** must not be
        // mistaken for an italic selection and reduced to *text*.
        return prefix != "*"
            || length == 2
            || selection[1] != '*'
            || selection[^2] != '*';
    }

    private static bool IsSelectionSurrounded(
        string text,
        int start,
        int length,
        string prefix,
        string suffix)
    {
        if (start < prefix.Length || start + length + suffix.Length > text.Length)
        {
            return false;
        }

        bool markersMatch = text.AsSpan(start - prefix.Length, prefix.Length)
                .SequenceEqual(prefix)
            && text.AsSpan(start + length, suffix.Length).SequenceEqual(suffix);
        if (!markersMatch || prefix != "*")
        {
            return markersMatch;
        }

        bool hasExtraOpeningAsterisk = start > prefix.Length
            && text[start - prefix.Length - 1] == '*';
        bool hasExtraClosingAsterisk = start + length + suffix.Length < text.Length
            && text[start + length + suffix.Length] == '*';
        return !hasExtraOpeningAsterisk && !hasExtraClosingAsterisk;
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
