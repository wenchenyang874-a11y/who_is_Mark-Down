namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Contains all data required to save a document without relying on UI state.
/// </summary>
public sealed record DocumentWriteRequest(
    string Path,
    string Text,
    bool EmitUtf8Bom);
