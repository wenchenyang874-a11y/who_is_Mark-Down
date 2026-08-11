using System.Text;
using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.Core.Tests.Documents;

public sealed class DocumentFileServiceBoundaryTests
{
    private readonly DocumentFileService service = new();

    [Theory]
    [InlineData("line one\rline two", DocumentLineEnding.Cr)]
    [InlineData("line one\r\nline two\n", DocumentLineEnding.Mixed)]
    [InlineData("one line", DocumentLineEnding.None)]
    public async Task ReadAsync_WhenLineEndingVaries_DetectsExpectedKind(
        string content,
        DocumentLineEnding expected)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "line-endings.md");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);

        LoadedDocument result = await service.ReadAsync(path, cancellationToken);

        Assert.Equal(expected, result.LineEnding);
    }

    [Fact]
    public void Inspect_WhenFileExists_ReturnsCurrentStamp()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "inspect.md");
        File.WriteAllText(path, "content");

        DocumentFileStamp result = service.Inspect(path);

        Assert.Equal(new FileInfo(path).Length, result.Length);
    }

    [Fact]
    public void Inspect_WhenFileDoesNotExist_ThrowsContextualException()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "missing.md");

        DocumentFileException exception = Assert.Throws<DocumentFileException>(
            () => service.Inspect(path));

        Assert.Equal(DocumentFileOperation.Inspect, exception.Operation);
        Assert.IsType<FileNotFoundException>(exception.InnerException);
    }

    [Fact]
    public async Task WriteAsync_WhenCreatingFileWithoutBom_WritesOnlyDocumentContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string path = System.IO.Path.Combine(temporaryDirectory.Path, "new.md");

        await service.WriteAsync(
            new DocumentWriteRequest(path, "plain text", EmitUtf8Bom: false),
            cancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        ReadOnlySpan<byte> utf8Bom = [0xEF, 0xBB, 0xBF];
        Assert.Equal("plain text", Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.AsSpan().StartsWith(utf8Bom));
    }
}
