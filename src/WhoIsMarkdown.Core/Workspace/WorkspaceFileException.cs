namespace WhoIsMarkdown.Core.Workspace;

public sealed class WorkspaceFileException : Exception
{
    public WorkspaceFileException(
        WorkspaceFileOperation operation,
        string path,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        Path = path;
    }

    public WorkspaceFileOperation Operation { get; }

    public string Path { get; }
}
