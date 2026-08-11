using System.Text;

namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Reads strict UTF-8 documents and saves them through a same-directory temporary
/// file. Replacing the destination only after a successful flush keeps the previous
/// document intact when encoding, disk, permission, or cancellation errors occur.
/// </summary>
public sealed class DocumentFileService : IDocumentFileService
{
    private const int FileBufferSize = 64 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<LoadedDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = NormalizePath(path);

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            bool hasBom = HasUtf8Bom(bytes);
            int contentOffset = hasBom ? Utf8Bom.Length : 0;
            string text = StrictUtf8.GetString(bytes, contentOffset, bytes.Length - contentOffset);

            return new LoadedDocument(
                fullPath,
                text,
                hasBom,
                DetectLineEnding(text),
                Inspect(fullPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            throw CreateException(DocumentFileOperation.Read, fullPath, exception);
        }
    }

    public async Task<DocumentFileStamp> WriteAsync(
        DocumentWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);

        string fullPath = NormalizePath(request.Path);
        string? directoryPath = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            throw new DocumentFileException(
                DocumentFileOperation.Write,
                fullPath,
                $"保存目录不存在：{directoryPath}",
                new DirectoryNotFoundException(directoryPath));
        }

        string temporaryPath = CreateTemporaryPath(directoryPath, fullPath);

        try
        {
            await WriteTemporaryFileAsync(
                    temporaryPath,
                    request.Text,
                    request.EmitUtf8Bom,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return Inspect(fullPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            throw CreateException(DocumentFileOperation.Write, fullPath, exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public DocumentFileStamp Inspect(string path)
    {
        string fullPath = NormalizePath(path);

        try
        {
            FileInfo fileInfo = new(fullPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("文档不存在。", fullPath);
            }

            return new DocumentFileStamp(fileInfo.Length, fileInfo.LastWriteTimeUtc);
        }
        catch (Exception exception) when (IsExpectedFileFailure(exception))
        {
            throw CreateException(DocumentFileOperation.Inspect, fullPath, exception);
        }
    }

    internal static DocumentLineEnding DetectLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int crLfCount = 0;
        int lfCount = 0;
        int crCount = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crLfCount++;
                    index++;
                }
                else
                {
                    crCount++;
                }
            }
            else if (current == '\n')
            {
                lfCount++;
            }
        }

        int kinds = (crLfCount > 0 ? 1 : 0) + (lfCount > 0 ? 1 : 0) + (crCount > 0 ? 1 : 0);
        if (kinds > 1)
        {
            return DocumentLineEnding.Mixed;
        }

        if (crLfCount > 0)
        {
            return DocumentLineEnding.CrLf;
        }

        if (lfCount > 0)
        {
            return DocumentLineEnding.Lf;
        }

        return crCount > 0 ? DocumentLineEnding.Cr : DocumentLineEnding.None;
    }

    private static async Task WriteTemporaryFileAsync(
        string temporaryPath,
        string text,
        bool emitUtf8Bom,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileBufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        if (emitUtf8Bom)
        {
            await stream.WriteAsync(Utf8Bom, cancellationToken).ConfigureAwait(false);
        }

        byte[] content = StrictUtf8.GetBytes(text);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return System.IO.Path.GetFullPath(path);
    }

    private static string CreateTemporaryPath(string directoryPath, string targetPath)
    {
        string fileName = System.IO.Path.GetFileName(targetPath);
        return System.IO.Path.Combine(directoryPath, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= Utf8Bom.Length
            && bytes[0] == Utf8Bom[0]
            && bytes[1] == Utf8Bom[1]
            && bytes[2] == Utf8Bom[2];
    }

    private static bool IsExpectedFileFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or EncoderFallbackException
            or NotSupportedException;
    }

    private static DocumentFileException CreateException(
        DocumentFileOperation operation,
        string path,
        Exception exception)
    {
        string action = operation switch
        {
            DocumentFileOperation.Read => "读取",
            DocumentFileOperation.Write => "保存",
            DocumentFileOperation.Inspect => "检查",
            _ => "处理",
        };

        return new DocumentFileException(
            operation,
            path,
            $"无法{action}文档“{path}”：{exception.Message}",
            exception);
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
            // The save result is already known. A stale temp file is safer than
            // masking the original failure or deleting an unexpected path.
        }
        catch (UnauthorizedAccessException)
        {
            // See the rationale above. Cleanup can be retried by maintenance code.
        }
    }
}
