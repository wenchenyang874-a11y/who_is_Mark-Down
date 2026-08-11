namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Wraps expected file-system and text-decoding failures with enough context for
/// the UI to show an actionable error while preserving the original exception.
/// </summary>
public sealed class DocumentFileException : Exception
{
    public DocumentFileException(
        DocumentFileOperation operation,
        string path,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Operation = operation;
        Path = path;
    }

    public DocumentFileOperation Operation { get; }

    public string Path { get; }
}
