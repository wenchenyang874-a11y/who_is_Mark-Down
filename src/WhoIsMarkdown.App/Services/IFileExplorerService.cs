namespace WhoIsMarkdown.App.Services;

public interface IFileExplorerService
{
    public void RevealFile(string path);

    public void RevealPath(string path);
}
