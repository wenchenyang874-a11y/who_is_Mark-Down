using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Resolves and saves images that are already visible in WIMD's generated preview.
/// Local virtual-host URLs remain confined to the Markdown directory, data images
/// are size-bounded, and remote downloads reapply the active trust policy to every
/// redirect before atomically replacing the user-selected destination.
/// </summary>
public sealed class PreviewImageSaveService : IDisposable
{
    public const long MaximumImageBytes = 32L * 1024 * 1024;

    private const int MaximumRedirects = 5;
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
    private readonly HttpClient httpClient;
    private bool disposed;

    public PreviewImageSaveService()
        : this(CreateHttpClient())
    {
    }

    public PreviewImageSaveService(HttpMessageHandler handler)
        : this(new HttpClient(
            handler ?? throw new ArgumentNullException(nameof(handler)),
            disposeHandler: true))
    {
    }

    private PreviewImageSaveService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
        this.httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public PreviewImageSaveSource Resolve(
        string source,
        string? documentPath,
        string? alternativeText,
        RemoteImagePolicy remoteImagePolicy)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(remoteImagePolicy);
        string candidate = source.Trim();

        if (candidate.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDataUri(candidate, alternativeText);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new PreviewImageSaveException("预览图片地址无效，无法保存。");
        }

        if (IsDocumentVirtualHost(uri))
        {
            return ResolveLocalFile(uri, documentPath, alternativeText);
        }

        if (!remoteImagePolicy.Allows(uri))
        {
            throw new PreviewImageSaveException("当前远程图片信任策略不允许保存此地址。");
        }

        string extension = ValidateExtension(Path.GetExtension(uri.AbsolutePath));
        string sourceName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return new PreviewImageSaveSource(
            PreviewImageSourceKind.RemoteHttps,
            uri.AbsoluteUri,
            extension,
            CreateSuggestedFileName(sourceName, alternativeText, extension),
            remoteImagePolicy);
    }

    /// <summary>
    /// Accepts only the base64 SVG emitted by WIMD's trusted Mermaid bridge. This
    /// path is deliberately separate from Resolve so Markdown-authored SVG data
    /// URIs and local SVG files remain blocked.
    /// </summary>
    public PreviewImageSaveSource ResolveGeneratedSvgDataUri(
        string source,
        string? alternativeText)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        const string header = "data:image/svg+xml;base64,";
        if (!source.StartsWith(header, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException("生成的 Mermaid 图表数据格式无效。");
        }

        string payload = source[header.Length..];
        byte[] bytes = DecodeDataUriBytes(payload);
        ValidateGeneratedSvg(bytes);
        return new PreviewImageSaveSource(
            PreviewImageSourceKind.GeneratedSvg,
            payload,
            ".svg",
            CreateSuggestedFileName(null, alternativeText ?? "Mermaid 图表", ".svg"));
    }

    public async Task<bool> SaveAsync(
        PreviewImageSaveSource source,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string target = Path.GetFullPath(targetPath);
        string targetExtension = Path.GetExtension(target);
        if (!targetExtension.Equals(source.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException($"保存文件的扩展名必须为 {source.Extension}。");
        }

        if (source.Kind == PreviewImageSourceKind.LocalFile
            && Path.GetFullPath(source.Value).Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string directory = Path.GetDirectoryName(target)
            ?? throw new PreviewImageSaveException("保存位置缺少有效的父目录。");
        if (!Directory.Exists(directory))
        {
            throw new PreviewImageSaveException("保存位置的父目录不存在。");
        }

        string temporaryPath = Path.Combine(directory, $".wimd-image-save-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await WriteSourceAsync(source, destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, target, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PreviewImageSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or HttpRequestException)
        {
            throw new PreviewImageSaveException($"无法保存预览图片：{exception.Message}", exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    /// <summary>
    /// Materializes a validated preview source once so the modeless image viewer
    /// can render and save the exact same bytes without repeating a remote request.
    /// </summary>
    public async Task<PreparedPreviewImage> PrepareAsync(
        PreviewImageSaveSource source,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        string directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PreviewImageSaveException("图片查看器缓存目录不能是符号链接或目录联接。");
        }

        string targetPath = Path.Combine(directory, source.SuggestedFileName);
        await SaveAsync(source, targetPath, cancellationToken).ConfigureAwait(false);
        return new PreparedPreviewImage(
            targetPath,
            source.Extension,
            source.SuggestedFileName,
            source.Kind is PreviewImageSourceKind.GeneratedSvg);
    }

    /// <summary>
    /// Saves a prepared viewer image through the same extension, size and atomic
    /// replacement checks used for direct preview saves.
    /// </summary>
    public async Task<bool> SavePreparedAsync(
        PreparedPreviewImage preparedImage,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(preparedImage);

        string preparedPath = Path.GetFullPath(preparedImage.FilePath);
        if (!File.Exists(preparedPath)
            || File.GetAttributes(preparedPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new PreviewImageSaveException("图片查看器中的临时图片已失效。");
        }

        FileInfo file = new(preparedPath);
        if (file.Length is <= 0 or > MaximumImageBytes)
        {
            throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
        }

        string extension;
        if (preparedImage.IsGeneratedSvg)
        {
            extension = Path.GetExtension(preparedPath).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? ".svg"
                : throw new PreviewImageSaveException("图片查看器中的 Mermaid 图表类型已改变。");
            ValidateGeneratedSvg(await File.ReadAllBytesAsync(preparedPath, cancellationToken)
                .ConfigureAwait(false));
        }
        else
        {
            extension = ValidateExtension(Path.GetExtension(preparedPath));
        }
        if (!extension.Equals(preparedImage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException("图片查看器中的临时图片类型已改变。");
        }

        PreviewImageSaveSource source = new(
            PreviewImageSourceKind.LocalFile,
            preparedPath,
            extension,
            preparedImage.SuggestedFileName);
        return await SaveAsync(source, targetPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new() { AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true);
    }

    private static PreviewImageSaveSource ResolveDataUri(
        string source,
        string? alternativeText)
    {
        int separator = source.IndexOf(',');
        if (separator <= 0)
        {
            throw new PreviewImageSaveException("内嵌图片数据格式无效。");
        }

        string header = source[..separator];
        string extension = header.ToLowerInvariant() switch
        {
            "data:image/png;base64" => ".png",
            "data:image/jpeg;base64" => ".jpg",
            "data:image/gif;base64" => ".gif",
            "data:image/bmp;base64" => ".bmp",
            "data:image/webp;base64" => ".webp",
            _ => throw new PreviewImageSaveException("仅支持保存 PNG、JPEG、GIF、BMP 和 WebP 图片。"),
        };
        string payload = source[(separator + 1)..];
        long estimatedBytes = ((long)payload.Length + 3) / 4 * 3;
        if (payload.Length == 0 || estimatedBytes > MaximumImageBytes)
        {
            throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
        }

        return new PreviewImageSaveSource(
            PreviewImageSourceKind.DataUri,
            payload,
            extension,
            CreateSuggestedFileName(null, alternativeText, extension));
    }

    private static PreviewImageSaveSource ResolveLocalFile(
        Uri uri,
        string? documentPath,
        string? alternativeText)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            throw new PreviewImageSaveException("当前文档尚未保存，无法定位这张本地图片。");
        }

        string document = Path.GetFullPath(documentPath);
        string directory = Path.GetDirectoryName(document)
            ?? throw new PreviewImageSaveException("当前文档缺少有效的父目录。");
        string[] encodedSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (encodedSegments.Length == 0)
        {
            throw new PreviewImageSaveException("本地预览图片地址缺少文件名。");
        }

        string candidate = directory;
        foreach (string encodedSegment in encodedSegments)
        {
            string segment;
            try
            {
                segment = Uri.UnescapeDataString(encodedSegment);
            }
            catch (UriFormatException exception)
            {
                throw new PreviewImageSaveException("本地预览图片地址编码无效。", exception);
            }

            if (segment is "." or ".." || segment.IndexOfAny(['/', '\\']) >= 0)
            {
                throw new PreviewImageSaveException("本地预览图片地址包含不安全的路径片段。");
            }

            candidate = Path.Combine(candidate, segment);
        }

        string fullPath = Path.GetFullPath(candidate);
        if (!IsWithinDirectory(fullPath, directory))
        {
            throw new PreviewImageSaveException("本地预览图片超出了当前文档目录。");
        }

        string extension = ValidateExtension(Path.GetExtension(fullPath));
        if (!File.Exists(fullPath))
        {
            throw new PreviewImageSaveException("预览中的本地图片已不存在。");
        }

        EnsureNoReparsePoint(directory, fullPath);
        return new PreviewImageSaveSource(
            PreviewImageSourceKind.LocalFile,
            fullPath,
            extension,
            CreateSuggestedFileName(Path.GetFileName(fullPath), alternativeText, extension));
    }

    private async Task WriteSourceAsync(
        PreviewImageSaveSource source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        switch (source.Kind)
        {
            case PreviewImageSourceKind.LocalFile:
                await using (FileStream input = new(
                    source.Value,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyWithLimitAsync(input, destination, cancellationToken).ConfigureAwait(false);
                }

                break;

            case PreviewImageSourceKind.DataUri:
            case PreviewImageSourceKind.GeneratedSvg:
                await WriteDataUriAsync(source.Value, destination, cancellationToken).ConfigureAwait(false);
                break;

            case PreviewImageSourceKind.RemoteHttps:
                await DownloadRemoteAsync(source, destination, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new PreviewImageSaveException("不支持的预览图片来源。");
        }
    }

    private static async Task WriteDataUriAsync(
        string payload,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] bytes = DecodeDataUriBytes(payload);

        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] DecodeDataUriBytes(string payload)
    {
        long estimatedBytes = ((long)payload.Length + 3) / 4 * 3;
        if (payload.Length == 0 || estimatedBytes > MaximumImageBytes)
        {
            throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(payload);
            if (bytes.Length == 0 || bytes.LongLength > MaximumImageBytes)
            {
                throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new PreviewImageSaveException("内嵌图片的 Base64 数据无效。", exception);
        }
    }

    private static void ValidateGeneratedSvg(byte[] bytes)
    {
        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumImageBytes,
            };
            using MemoryStream stream = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement root = document.Root
                ?? throw new PreviewImageSaveException("生成的 Mermaid SVG 缺少根元素。");
            if (!root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)
                || root.Name.NamespaceName != "http://www.w3.org/2000/svg")
            {
                throw new PreviewImageSaveException("生成的 Mermaid 图表不是有效的 SVG。");
            }

            HashSet<string> blockedElements = new(StringComparer.OrdinalIgnoreCase)
            {
                "script", "foreignObject", "iframe", "object", "embed", "image", "audio",
                "video", "link", "meta",
            };
            foreach (XElement element in root.DescendantsAndSelf())
            {
                if (blockedElements.Contains(element.Name.LocalName))
                {
                    throw new PreviewImageSaveException("生成的 Mermaid SVG 包含不安全元素。");
                }

                foreach (XAttribute attribute in element.Attributes())
                {
                    string name = attribute.Name.LocalName;
                    string value = attribute.Value.Trim();
                    if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("src", StringComparison.OrdinalIgnoreCase)
                        || ((name.Equals("href", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                            && !value.StartsWith('#'))
                        || (name.Equals("style", StringComparison.OrdinalIgnoreCase)
                            && HasUnsafeSvgCss(value)))
                    {
                        throw new PreviewImageSaveException("生成的 Mermaid SVG 包含不安全属性。");
                    }
                }

                if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)
                    && HasUnsafeSvgCss(element.Value))
                {
                    throw new PreviewImageSaveException("生成的 Mermaid SVG 包含不安全样式。");
                }
            }
        }
        catch (PreviewImageSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new PreviewImageSaveException("生成的 Mermaid SVG 无法通过安全校验。", exception);
        }
    }

    private static bool HasUnsafeSvgCss(string value)
    {
        if (Regex.IsMatch(
                value,
                "@import|expression\\s*\\(|javascript\\s*:|data\\s*:|https?\\s*:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))
        {
            return true;
        }

        foreach (Match match in Regex.Matches(
                     value,
                     "url\\s*\\(([^)]*)\\)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(50)))
        {
            string target = match.Groups[1].Value.Trim().Trim('\'', '"');
            if (!target.StartsWith('#'))
            {
                return true;
            }
        }

        return false;
    }

    private async Task DownloadRemoteAsync(
        PreviewImageSaveSource source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        RemoteImagePolicy policy = source.RemoteImagePolicy
            ?? throw new PreviewImageSaveException("远程图片缺少信任策略。");
        Uri current = new(source.Value, UriKind.Absolute);
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, current);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects || response.Headers.Location is null)
                {
                    throw new PreviewImageSaveException("远程图片重定向次数过多或目标无效。");
                }

                Uri next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (!policy.Allows(next))
                {
                    throw new PreviewImageSaveException("远程图片重定向到了未受信任的地址。");
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PreviewImageSaveException(
                    $"远程图片下载失败（HTTP {(int)response.StatusCode}）。");
            }

            ValidateRemoteResponse(response.Content.Headers, source.Extension);
            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await CopyWithLimitAsync(input, destination, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static void ValidateRemoteResponse(HttpContentHeaders headers, string extension)
    {
        if (headers.ContentLength is <= 0 or > MaximumImageBytes)
        {
            throw new PreviewImageSaveException("远程图片为空或超过 32 MB，无法保存。");
        }

        string? mediaType = headers.ContentType?.MediaType;
        bool mediaTypeMatches = extension.ToLowerInvariant() switch
        {
            ".png" => MediaTypeEquals(mediaType, "image/png"),
            ".jpg" or ".jpeg" => MediaTypeEquals(mediaType, "image/jpeg"),
            ".gif" => MediaTypeEquals(mediaType, "image/gif"),
            ".bmp" => MediaTypeEquals(mediaType, "image/bmp")
                || MediaTypeEquals(mediaType, "image/x-ms-bmp"),
            ".webp" => MediaTypeEquals(mediaType, "image/webp"),
            _ => false,
        };
        if (!mediaTypeMatches)
        {
            throw new PreviewImageSaveException("远程服务器返回的内容类型与图片扩展名不一致。");
        }
    }

    private static bool MediaTypeEquals(string? value, string expected)
    {
        return value?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumImageBytes)
            {
                throw new PreviewImageSaveException("预览图片超过 32 MB，无法保存。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        if (total == 0)
        {
            throw new PreviewImageSaveException("预览图片为空，无法保存。");
        }
    }

    private static string ValidateExtension(string extension)
    {
        if (!SupportedExtensions.Contains(extension))
        {
            throw new PreviewImageSaveException("仅支持保存 PNG、JPEG、GIF、BMP 和 WebP 图片。");
        }

        return extension.ToLowerInvariant();
    }

    private static bool IsDocumentVirtualHost(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.IdnHost.Equals(LocalImageUrlResolver.VirtualHostName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinDirectory(string candidate, string directory)
    {
        string relative = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePoint(string directory, string filePath)
    {
        string current = directory;
        string relative = Path.GetRelativePath(directory, filePath);
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new PreviewImageSaveException("本地图片路径包含符号链接或目录联接，已取消保存。");
            }
        }
    }

    private static string CreateSuggestedFileName(
        string? sourceName,
        string? alternativeText,
        string extension)
    {
        string candidate = !string.IsNullOrWhiteSpace(sourceName)
            ? Path.GetFileNameWithoutExtension(sourceName)
            : alternativeText ?? "image";
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = new string(candidate.Trim().Select(character =>
            invalidCharacters.Contains(character) ? '-' : character).ToArray()).Trim('.', ' ');
        if (sanitized.Length == 0 || ReservedDeviceNames.Contains(sanitized))
        {
            sanitized = "image";
        }

        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80];
        }

        return string.Concat(sanitized, extension);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
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
            // Cleanup is best-effort and must not replace the original save error.
        }
    }
}
