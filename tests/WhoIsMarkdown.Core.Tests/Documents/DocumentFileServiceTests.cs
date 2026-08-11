using System.Text;
using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.Core.Tests.Documents;

public sealed class DocumentFileServiceTests
{
    private readonly DocumentFileService service = new();

    [Fact]
    public async Task ReadAsync_WhenPathContainsChineseCharacters_PreservesTextAndLineEndings()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string documentDirectory = System.IO.Path.Combine(temporaryDirectory.Path, "中文目录");
        Directory.CreateDirectory(documentDirectory);
        string documentPath = System.IO.Path.Combine(documentDirectory, "说明.md");
        const string expectedText = "# 标题\r\n\r\n这是正文。\r\n";
        await File.WriteAllTextAsync(
            documentPath,
            expectedText,
            new UTF8Encoding(false),
            cancellationToken);

        LoadedDocument result = await service.ReadAsync(documentPath, cancellationToken);

        Assert.Equal(System.IO.Path.GetFullPath(documentPath), result.Path);
        Assert.Equal(expectedText, result.Text);
        Assert.False(result.HasUtf8Bom);
        Assert.Equal(DocumentLineEnding.CrLf, result.LineEnding);
        Assert.Equal(new FileInfo(documentPath).Length, result.Stamp.Length);
    }

    [Fact]
    public async Task ReadAsync_WhenFileHasUtf8Bom_ReportsBomWithoutReturningBomCharacter()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "bom.md");
        byte[] content = Encoding.UTF8.GetBytes("正文\n");
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. content];
        await File.WriteAllBytesAsync(documentPath, bytes, cancellationToken);

        LoadedDocument result = await service.ReadAsync(documentPath, cancellationToken);

        Assert.True(result.HasUtf8Bom);
        Assert.Equal("正文\n", result.Text);
        Assert.Equal(DocumentLineEnding.Lf, result.LineEnding);
    }

    [Fact]
    public async Task ReadAsync_WhenUtf8IsInvalid_ThrowsContextualException()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "invalid.md");
        await File.WriteAllBytesAsync(documentPath, [0xC3, 0x28], cancellationToken);

        DocumentFileException exception = await Assert.ThrowsAsync<DocumentFileException>(
            () => service.ReadAsync(documentPath, cancellationToken));

        Assert.Equal(DocumentFileOperation.Read, exception.Operation);
        Assert.Equal(System.IO.Path.GetFullPath(documentPath), exception.Path);
        Assert.IsType<DecoderFallbackException>(exception.InnerException);
    }

    [Fact]
    public async Task WriteAsync_WhenTargetExists_ReplacesContentWithoutLeavingTemporaryFile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "notes.md");
        await File.WriteAllTextAsync(
            documentPath,
            "旧内容",
            new UTF8Encoding(false),
            cancellationToken);
        DocumentWriteRequest request = new(documentPath, "新内容\r\n", EmitUtf8Bom: true);

        DocumentFileStamp stamp = await service.WriteAsync(request, cancellationToken);

        byte[] bytes = await File.ReadAllBytesAsync(documentPath, cancellationToken);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Equal("新内容\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(bytes.LongLength, stamp.Length);
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".notes.md.*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_WhenCancelledBeforeReplacement_PreservesExistingFile()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory temporaryDirectory = new();
        string documentPath = System.IO.Path.Combine(temporaryDirectory.Path, "cancelled.md");
        await File.WriteAllTextAsync(
            documentPath,
            "原内容",
            new UTF8Encoding(false),
            testCancellationToken);
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WriteAsync(
                new DocumentWriteRequest(documentPath, "不应写入", EmitUtf8Bom: false),
                cancellationTokenSource.Token));

        Assert.Equal(
            "原内容",
            await File.ReadAllTextAsync(documentPath, testCancellationToken));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, ".cancelled.md.*.tmp"));
    }
}
