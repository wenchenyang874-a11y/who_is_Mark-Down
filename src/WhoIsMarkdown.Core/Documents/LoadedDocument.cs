namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Immutable result of reading a Markdown document from disk.
/// </summary>
public sealed record LoadedDocument(
    string Path,
    string Text,
    bool HasUtf8Bom,
    DocumentLineEnding LineEnding,
    DocumentFileStamp Stamp);
