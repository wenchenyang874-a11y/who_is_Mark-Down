namespace WhoIsMarkdown.Core.Editing;

public sealed record MarkdownTaskStateEdit(int Offset, char Replacement, bool HasChanged);

