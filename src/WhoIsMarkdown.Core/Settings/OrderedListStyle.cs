namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Visual treatment only: keep the browser's native list-item counter, source
/// numbering, start values and nesting rather than rewriting Markdown content.
/// </summary>
public enum OrderedListStyle
{
    Plain,
    Emphasis,
    Circle,
    RoundedSquare,
}
