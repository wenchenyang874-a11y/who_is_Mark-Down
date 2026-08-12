namespace WhoIsMarkdown.Core.Editing;

public sealed record MarkdownTextEdit(
    string Text,
    int SelectionStart,
    int SelectionLength,
    int CaretOffset);
