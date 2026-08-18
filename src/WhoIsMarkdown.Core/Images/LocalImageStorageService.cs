namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Stores inserted images below the current Markdown document directory. Every
/// configured segment is validated before I/O, and existing reparse points are
/// rejected so a seemingly relative image folder cannot escape through a junction.
/// </summary>
public sealed class LocalImageStorageService
{
    public const string DefaultRelativeDirectory = "./img/";

    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".webp",
    };

    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();

    public static async Task<StoredLocalImage> StoreFileAsync(
        string documentPath,
        string relativeDirectory,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new LocalImageStorageException($"待插入的图片不存在：{source}");
        }

        string extension = ValidateExtension(Path.GetExtension(source));
        string preferredName = Path.GetFileNameWithoutExtension(source);
        await using FileStream stream = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await StoreStreamAsync(
            documentPath,
            relativeDirectory,
            preferredName,
            extension,
            stream,
            cancellationToken).ConfigureAwait(false);
    }

    public static Task<StoredLocalImage> StorePngAsync(
        string documentPath,
        string relativeDirectory,
        string preferredName,
        ReadOnlyMemory<byte> pngBytes,
        CancellationToken cancellationToken = default)
    {
        if (pngBytes.IsEmpty)
        {
            throw new ArgumentException("图片数据不能为空。", nameof(pngBytes));
        }

        MemoryStream stream = new(pngBytes.ToArray(), writable: false);
        return StoreOwnedStreamAsync(
            documentPath,
            relativeDirectory,
            preferredName,
            ".png",
            stream,
            cancellationToken);
    }

    public static string NormalizeRelativeDirectory(string relativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeDirectory);
        string normalized = relativeDirectory.Trim().Replace('\\', '/');
        if (Path.IsPathFullyQualified(normalized) || normalized.StartsWith('/'))
        {
            throw new ArgumentException("图片目录必须是相对于当前 Markdown 文件的路径。", nameof(relativeDirectory));
        }

        List<string> segments = [];
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            ValidateDirectorySegment(segment, relativeDirectory);
            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException("图片目录至少需要包含一个文件夹名称。", nameof(relativeDirectory));
        }

        return $"./{string.Join('/', segments)}/";
    }

    private static async Task<StoredLocalImage> StoreOwnedStreamAsync(
        string documentPath,
        string relativeDirectory,
        string preferredName,
        string extension,
        Stream stream,
        CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            return await StoreStreamAsync(
                documentPath,
                relativeDirectory,
                preferredName,
                extension,
                stream,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<StoredLocalImage> StoreStreamAsync(
        string documentPath,
        string relativeDirectory,
        string preferredName,
        string extension,
        Stream source,
        CancellationToken cancellationToken)
    {
        string documentDirectory = GetDocumentDirectory(documentPath);
        string normalizedDirectory = NormalizeRelativeDirectory(relativeDirectory);
        string targetDirectory = ResolveTargetDirectory(documentDirectory, normalizedDirectory);
        string safeBaseName = NormalizeBaseName(preferredName);
        string temporaryPath = Path.Combine(targetDirectory, $".wimd-image-{Guid.NewGuid():N}.tmp");

        try
        {
            EnsureNoReparsePoint(documentDirectory, targetDirectory);
            Directory.CreateDirectory(targetDirectory);
            EnsureNoReparsePoint(documentDirectory, targetDirectory);

            // Write the complete image before exposing its final name. A failed or
            // canceled copy therefore cannot leave a partially rendered asset in
            // the user's document directory.
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            for (int suffix = 1; suffix <= 10_000; suffix++)
            {
                string fileName = suffix == 1
                    ? string.Concat(safeBaseName, extension)
                    : $"{safeBaseName}-{suffix}{extension}";
                string targetPath = Path.Combine(targetDirectory, fileName);
                try
                {
                    File.Move(temporaryPath, targetPath);
                    return new StoredLocalImage(
                        targetPath,
                        CreateMarkdownPath(documentDirectory, targetPath));
                }
                catch (IOException) when (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    // Another image already owns this name; try the next suffix.
                }
            }

            throw new IOException("无法为图片生成不重复的文件名。");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new LocalImageStorageException(
                $"无法把图片保存到“{normalizedDirectory}”：{exception.Message}",
                exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not replace the original image error.
        }
    }
    private static string GetDocumentDirectory(string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        string fullPath = Path.GetFullPath(documentPath);
        return Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Markdown 文件缺少父目录。", nameof(documentPath));
    }

    private static string ResolveTargetDirectory(string documentDirectory, string normalizedDirectory)
    {
        string relative = normalizedDirectory[2..^1].Replace('/', Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(relative, documentDirectory);
        string boundary = Path.GetRelativePath(documentDirectory, target);
        if (Path.IsPathFullyQualified(boundary)
            || boundary.Equals("..", StringComparison.Ordinal)
            || boundary.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || boundary.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("图片目录不能超出当前 Markdown 文件所在目录。", nameof(normalizedDirectory));
        }

        return target;
    }

    private static void EnsureNoReparsePoint(string documentDirectory, string targetDirectory)
    {
        string current = documentDirectory;
        string relative = Path.GetRelativePath(documentDirectory, targetDirectory);
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("图片目录包含目录联接或符号链接，已取消保存以防止越界。");
            }
        }
    }

    private static string NormalizeBaseName(string preferredName)
    {
        string candidate = string.IsNullOrWhiteSpace(preferredName) ? "image" : preferredName.Trim();
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = new string(candidate.Select(character =>
            invalidCharacters.Contains(character) ? '-' : character).ToArray()).Trim('.', ' ');
        if (sanitized.Length == 0 || ReservedDeviceNames.Contains(sanitized))
        {
            return "image";
        }

        return sanitized.Length <= 80 ? sanitized : sanitized[..80];
    }

    private static string CreateMarkdownPath(string documentDirectory, string imagePath)
    {
        string relative = Path.GetRelativePath(documentDirectory, imagePath).Replace('\\', '/');
        return relative.StartsWith("./", StringComparison.Ordinal) ? relative : $"./{relative}";
    }

    private static string ValidateExtension(string extension)
    {
        if (!SupportedExtensions.Contains(extension))
        {
            throw new LocalImageStorageException("仅支持 PNG、JPEG、GIF、BMP 和 WebP 图片。");
        }

        return extension.ToLowerInvariant();
    }

    private static void ValidateDirectorySegment(string segment, string parameterValue)
    {
        if (segment == ".."
            || segment.EndsWith('.')
            || segment.EndsWith(' ')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(segment)))
        {
            throw new ArgumentException(
                "图片目录不能包含 ..、Windows 保留名称或无效字符。",
                nameof(parameterValue));
        }
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (int number = 1; number <= 9; number++)
        {
            names.Add($"COM{number}");
            names.Add($"LPT{number}");
        }

        return names;
    }
}
