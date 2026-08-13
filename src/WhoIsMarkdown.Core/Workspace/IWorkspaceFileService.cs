namespace WhoIsMarkdown.Core.Workspace;

public interface IWorkspaceFileService
{
    public string Open(string rootPath);

    public IReadOnlyList<WorkspaceEntry> GetChildren(string rootPath, string directoryPath);

    public string CreateMarkdownFile(string rootPath, string parentDirectoryPath, string name);

    public string CreateDirectory(string rootPath, string parentDirectoryPath, string name);

    public string Rename(string rootPath, string entryPath, string newName);

    public void Delete(string rootPath, string entryPath);
}
