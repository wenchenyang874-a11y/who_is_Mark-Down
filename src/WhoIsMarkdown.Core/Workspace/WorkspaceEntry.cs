namespace WhoIsMarkdown.Core.Workspace;

public sealed record WorkspaceEntry(
    string Path,
    string Name,
    bool IsDirectory);
