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
