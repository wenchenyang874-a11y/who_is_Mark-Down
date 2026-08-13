using WhoIsMarkdown.Core.Workspace;

namespace WhoIsMarkdown.Core.Tests.Workspace;

public sealed class WorkspaceFileServiceTests
{
    private readonly WorkspaceFileService service = new();

    [Fact]
    public void 枚举_工作区包含多种文件_只返回目录和Markdown文件()
    {
        using TemporaryDirectory temporaryDirectory = new();
        Directory.CreateDirectory(Path.Combine(temporaryDirectory.Path, "文档"));
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "说明.md"), "# 说明");
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "notes.markdown"), "notes");
        File.WriteAllText(Path.Combine(temporaryDirectory.Path, "secret.txt"), "hidden");

        IReadOnlyList<WorkspaceEntry> entries = service.GetChildren(
            temporaryDirectory.Path,
            temporaryDirectory.Path);

        Assert.Equal(["文档", "notes.markdown", "说明.md"], entries.Select(entry => entry.Name));
        Assert.True(entries[0].IsDirectory);
        Assert.DoesNotContain(entries, entry => entry.Name == "secret.txt");
    }

    [Fact]
    public void 新建_名称没有扩展名_创建UTF8Markdown文件()
    {
        using TemporaryDirectory temporaryDirectory = new();

        string path = service.CreateMarkdownFile(
            temporaryDirectory.Path,
            temporaryDirectory.Path,
            "新文档");

        Assert.Equal("新文档.md", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllBytes(path));
    }

    [Fact]
    public void 重命名_文件和目录位于工作区内_移动到新名称()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string folder = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "旧目录")).FullName;
        string document = Path.Combine(folder, "旧名称.md");
        File.WriteAllText(document, "content");

        string renamedDocument = service.Rename(temporaryDirectory.Path, document, "新名称");
        string renamedFolder = service.Rename(temporaryDirectory.Path, folder, "新目录");

        Assert.False(File.Exists(document));
        Assert.True(File.Exists(Path.Combine(renamedFolder, "新名称.md")));
        Assert.Equal("新名称.md", Path.GetFileName(renamedDocument));
    }

    [Fact]
    public void 删除_非空目录位于工作区内_递归删除真实内容()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string folder = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "待删除")).FullName;
        File.WriteAllText(Path.Combine(folder, "文档.md"), "content");

        service.Delete(temporaryDirectory.Path, folder);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void 删除_目标是工作区根目录_拒绝操作并保留目录()
    {
        using TemporaryDirectory temporaryDirectory = new();

        WorkspaceFileException exception = Assert.Throws<WorkspaceFileException>(
            () => service.Delete(temporaryDirectory.Path, temporaryDirectory.Path));

        Assert.Contains("不能是工作区根目录", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(temporaryDirectory.Path));
    }

    [Fact]
    public void 新建_父目录位于工作区之外_拒绝路径越界()
    {
        using TemporaryDirectory workspace = new();
        using TemporaryDirectory outside = new();

        WorkspaceFileException exception = Assert.Throws<WorkspaceFileException>(
            () => service.CreateMarkdownFile(workspace.Path, outside.Path, "越界.md"));

        Assert.Contains("必须位于当前工作区内", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside.Path, "越界.md")));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("CON.md")]
    [InlineData(" trailing ")]
    [InlineData("trailing.")]
    [InlineData("wrong.txt")]
    public void 新建_名称不安全或格式不支持_拒绝操作(string name)
    {
        using TemporaryDirectory temporaryDirectory = new();

        Assert.Throws<ArgumentException>(
            () => service.CreateMarkdownFile(
                temporaryDirectory.Path,
                temporaryDirectory.Path,
                name));
    }
}
