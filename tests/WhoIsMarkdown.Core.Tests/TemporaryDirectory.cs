namespace WhoIsMarkdown.Core.Tests;

/// <summary>
/// Creates an isolated test directory and deletes only descendants of the fixed
/// WhoIsMarkdown.Tests temp root. The boundary check prevents a malformed path
/// from turning test cleanup into a broad recursive delete.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WhoIsMarkdown.Tests"));

    public TemporaryDirectory()
    {
        Directory.CreateDirectory(TestRoot);
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        string fullPath = System.IO.Path.GetFullPath(Path);
        string requiredPrefix = TestRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝清理测试根目录之外的路径：{fullPath}");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}
