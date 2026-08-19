using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Editing;

/// <summary>
/// Resolves a preview task checkbox back to the single Markdown state character
/// that may be changed. Keeping this parser in Core makes the WebView message an
/// untrusted request rather than permission to replace arbitrary editor text.
/// </summary>
public static partial class MarkdownTaskListService
{
    public static bool TryCreateStateEdit(
        string text,
        int zeroBasedLine,
        bool isCompleted,
        out MarkdownTaskStateEdit? edit)
    {
        ArgumentNullException.ThrowIfNull(text);
        edit = null;
        if (zeroBasedLine < 0 || !TryFindLine(text, zeroBasedLine, out int lineStart, out int lineLength))
        {
            return false;
        }

        string lineText = text.Substring(lineStart, lineLength);
        Match match = TaskListMarkerRegex().Match(lineText);
        if (!match.Success)
        {
            return false;
        }

        Group state = match.Groups["state"];
        edit = new MarkdownTaskStateEdit(
            lineStart + state.Index,
            isCompleted ? 'x' : ' ',
            text[lineStart + state.Index] != (isCompleted ? 'x' : ' '));
        return true;
    }

    private static bool TryFindLine(
        string text,
        int targetLine,
        out int lineStart,
        out int lineLength)
    {
        lineStart = 0;
        for (int line = 0; line < targetLine; line++)
        {
            int lineBreak = text.IndexOf('\n', lineStart);
            if (lineBreak < 0)
            {
                lineLength = 0;
                return false;
            }

            lineStart = lineBreak + 1;
        }

        int lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
        {
            lineEnd--;
        }

        lineLength = lineEnd - lineStart;
        return true;
    }

    [GeneratedRegex(
        "^[ \\t]*(?:[-+*]|[0-9]+[.)])[ \\t]+\\[(?<state>[ xX])\\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TaskListMarkerRegex();
}
