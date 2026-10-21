using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WhoIsMarkdown.Core.Diagnostics;
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
        ".svg",
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

        string urlExtension = Path.GetExtension(uri.AbsolutePath);
        string extension = SupportedExtensions.Contains(urlExtension) ? urlExtension.ToLowerInvariant() : ".img";
        string sourceName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return new PreviewImageSaveSource(
            PreviewImageSourceKind.RemoteHttps,
            uri.AbsoluteUri,
            extension,
            CreateSuggestedFileName(sourceName, alternativeText, extension),
            remoteImagePolicy);
    }

    /// <summary>
    /// Accepts base64 SVG emitted by WIMD's Mermaid bridge. Its own output
    /// contract is checked separately from unrestricted author-provided images.
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

        // URL suffixes are only hints. Materialize once and use the response's
        // actual image encoding for both the viewer and the save dialog.
        if (source.RequiresNetwork)
        {
            (byte[] bytes, string extension) = await MaterializeRemoteAsync(source, cancellationToken).ConfigureAwait(false);
            return await SaveBytesAsync(bytes, extension, targetPath, cancellationToken).ConfigureAwait(false);
        }

        string target = Path.GetFullPath(targetPath);
        string targetExtension = Path.GetExtension(target);
        if (!targetExtension.Equals(source.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException($"保存文件的扩展名必须为 {source.Extension}。");
        }

        if (source.Kind is PreviewImageSourceKind.LocalFile or PreviewImageSourceKind.LocalSvg
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
                await CoreOperationTrace.MeasureAsync("ImageSourceCopy", () => WriteSourceAsync(source, destination, cancellationToken)).ConfigureAwait(false);
                await CoreOperationTrace.MeasureAsync("ImageCacheFlush", () => destination.FlushAsync(cancellationToken)).ConfigureAwait(false);
            }

            CoreOperationTrace.Measure("ImageCachePublish", () => { File.Move(temporaryPath, target, overwrite: true); return true; });
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

        string directory = CreateCacheDirectory(cacheDirectory);

        if (source.RequiresNetwork)
        {
            (byte[] bytes, string extension) = await MaterializeRemoteAsync(source, cancellationToken).ConfigureAwait(false);
            string name = CreateSuggestedFileName(source.SuggestedFileName, null, extension);
            string remoteTarget = Path.Combine(directory, name);
            await SaveBytesCoreAsync(bytes, extension, remoteTarget, durable: false, cancellationToken).ConfigureAwait(false);
            return new PreparedPreviewImage(remoteTarget, extension, name, isSourceSvg: extension == ".svg");
        }

        string targetPath = Path.Combine(directory, source.SuggestedFileName);
        await SaveAsync(source, targetPath, cancellationToken).ConfigureAwait(false);
        return new PreparedPreviewImage(
            targetPath,
            source.Extension,
            source.SuggestedFileName,
            source.Kind is PreviewImageSourceKind.GeneratedSvg,
            source.Extension == ".svg" && source.Kind is not PreviewImageSourceKind.GeneratedSvg);
    }

    /// <summary>
    /// Reuses the response already downloaded by Chromium; this must not issue a
    /// second HTTP request. The temporary viewer copy is atomic but not durable:
    /// unlike an explicit save it need not force the disk cache through to hardware.
    /// </summary>
    public async Task<PreparedPreviewImage> PrepareCachedAsync(PreviewImageSaveSource source,
        byte[] bytes, string contentType, string cacheDirectory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is <= 0 || bytes.LongLength > MaximumImageBytes)
            throw new PreviewImageSaveException("缓存图片为空或超过 32 MB。");
        string directory = CreateCacheDirectory(cacheDirectory);
        string extension = CoreOperationTrace.Measure("ImageFormatDetect", () =>
            DetectImageExtension(bytes, ResponseExtension(contentType, source.Extension)));
        byte[] materialized = extension == ".svg" ? NormalizeSvgSize(bytes) : bytes;
        string name = CreateSuggestedFileName(source.SuggestedFileName, null, extension);
        string target = Path.Combine(directory, name);
        await SaveBytesCoreAsync(materialized, extension, target, durable: false, cancellationToken).ConfigureAwait(false);
        return new PreparedPreviewImage(target, extension, name, isSourceSvg: extension == ".svg");
    }

    private static string CreateCacheDirectory(string cacheDirectory) => CoreOperationTrace.Measure("ImageCacheDirectory", () =>
    {
        string directory = Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new PreviewImageSaveException("图片查看器缓存目录不能是符号链接或目录联接。");
        return directory;
    });

    /// <summary>
    /// Re-runs the prepared-image checks and returns the validated cache path together
    /// with its normalized extension. Saving and exporting both repeat these checks
    /// because the cache file can be replaced between the preview and the operation,
    /// and an export format such as PNG or HTML must never trust bytes it did not
    /// revalidate itself.
    /// </summary>
    internal async Task<(string Path, string Extension)> ValidatePreparedAsync(
        PreparedPreviewImage preparedImage,
        CancellationToken cancellationToken)
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
        if (preparedImage.IsSvg)
        {
            extension = Path.GetExtension(preparedPath).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? ".svg"
                : throw new PreviewImageSaveException("图片查看器中的 SVG 类型已改变。");
            // Preserve author SVG content. Cache existence/type/size checks above
            // protect file operations, not a content allowlist. Chromium displays
            // the resulting SVG as an image, never as a navigable document.
        }
        else
        {
            extension = ValidateExtension(Path.GetExtension(preparedPath));
        }

        if (!extension.Equals(preparedImage.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException("图片查看器中的临时图片类型已改变。");
        }

        return (preparedPath, extension);
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

        (string preparedPath, string extension) = await ValidatePreparedAsync(
            preparedImage,
            cancellationToken).ConfigureAwait(false);
        PreviewImageSaveSource source = new(
            preparedImage.IsSourceSvg
                ? PreviewImageSourceKind.LocalSvg
                : PreviewImageSourceKind.LocalFile,
            preparedPath,
            extension,
            preparedImage.SuggestedFileName);
        return await SaveAsync(source, targetPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically replaces the destination with bytes that were produced in memory.
    /// A rasterized PNG and a generated HTML page never exist as a source file, but
    /// they must still reach the disk through the same temporary-file replacement as
    /// every other image save so a failure never leaves a half-written target.
    /// </summary>
    public Task<bool> SaveBytesAsync(
        byte[] bytes,
        string extension,
        string targetPath,
        CancellationToken cancellationToken = default) => SaveBytesCoreAsync(bytes, extension, targetPath, durable: true, cancellationToken);

    private async Task<bool> SaveBytesCoreAsync(byte[] bytes, string extension, string targetPath,
        bool durable, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (bytes.Length is <= 0 || bytes.LongLength > MaximumImageBytes)
        {
            throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
        }

        string target = Path.GetFullPath(targetPath);
        if (!Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewImageSaveException($"保存文件的扩展名必须为 {extension}。");
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
                FileOptions.Asynchronous | (durable ? FileOptions.WriteThrough : FileOptions.None)))
            {
                await CoreOperationTrace.MeasureAsync("ImageCacheWrite", () => destination.WriteAsync(bytes, cancellationToken).AsTask()).ConfigureAwait(false);
                CoreOperationTrace.Value("ImageCacheBytes", bytes.LongLength);
                await CoreOperationTrace.MeasureAsync("ImageCacheFlush", () => destination.FlushAsync(cancellationToken)).ConfigureAwait(false);
            }

            CoreOperationTrace.Measure("ImageCachePublish", () => { File.Move(temporaryPath, target, overwrite: true); return true; });
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
            or PathTooLongException)
        {
            throw new PreviewImageSaveException($"无法保存预览图片：{exception.Message}", exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
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
        if (separator <= 0) throw new PreviewImageSaveException("内嵌图片地址无效。");
        string header = source[..separator];
        string mediaType = header.Split(';')[0].ToLowerInvariant();
        string extension = mediaType switch
        {
            "data:image/png" => ".png",
            "data:image/jpeg" => ".jpg",
            "data:image/gif" => ".gif",
            "data:image/bmp" => ".bmp",
            "data:image/webp" => ".webp",
            "data:image/svg+xml" => ".svg",
            _ => throw new PreviewImageSaveException("无法识别内嵌图片的格式。"),
        };
        string payload = source[(separator + 1)..];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)));
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

        string extension = ValidateLocalExtension(Path.GetExtension(fullPath));
        if (!File.Exists(fullPath))
        {
            throw new PreviewImageSaveException("预览中的本地图片已不存在。");
        }

        EnsureNoReparsePoint(directory, fullPath);
        return new PreviewImageSaveSource(
            extension == ".svg" ? PreviewImageSourceKind.LocalSvg : PreviewImageSourceKind.LocalFile,
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

            case PreviewImageSourceKind.LocalSvg:
                if (new FileInfo(source.Value).Length is <= 0 or > MaximumImageBytes)
                    throw new PreviewImageSaveException("预览图片为空或超过 32 MB，无法保存。");
                await WriteVectorAsync(
                    await File.ReadAllBytesAsync(source.Value, cancellationToken).ConfigureAwait(false),
                    destination, cancellationToken).ConfigureAwait(false);
                break;

            case PreviewImageSourceKind.GeneratedSvg:
                await WriteVectorAsync(
                    DecodeDataUriBytes(source.Value),
                    destination,
                    cancellationToken).ConfigureAwait(false);
                break;

            case PreviewImageSourceKind.DataUri:
                if (source.Extension == ".svg")
                    await WriteVectorAsync(DecodeDataUriBytes(source.Value), destination, cancellationToken).ConfigureAwait(false);
                else
                    await WriteDataUriAsync(source.Value, destination, cancellationToken).ConfigureAwait(false);
                break;

            case PreviewImageSourceKind.RemoteHttps:
                await DownloadRemoteAsync(source, destination, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new PreviewImageSaveException("不支持的预览图片来源。");
        }
    }

    /// <summary>
    /// Writes an SVG after resolving its pixel size, without filtering its content.
    ///
    /// The size has to be baked in here rather than by each consumer: an image element
    /// cannot read the vector's viewBox back, and Chromium reports its 300x150 default
    /// for a percentage-sized SVG. The independent viewer trusted that number and
    /// showed the diagram at several times its real scale, pinned to the left edge, and
    /// a standalone HTML export rendered it only a few hundred pixels wide.
    /// </summary>
    private static async Task WriteVectorAsync(
        byte[] svgBytes,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] sized = NormalizeSvgSize(svgBytes);
        await destination.WriteAsync(sized, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] NormalizeSvgSize(byte[] svgBytes) =>
        CoreOperationTrace.Measure("ImageVectorNormalize", () => NormalizeSvgSizeCore(svgBytes));

    private static byte[] NormalizeSvgSizeCore(byte[] svgBytes)
    {
        // Sizing is a display aid, never an admission test for author SVG. A valid
        // browser SVG may use XML constructs our small size reader does not know;
        // preserve its bytes rather than turning that limitation into a block.
        try { return SvgPixelSizeNormalizer.Apply(svgBytes).Bytes; }
        catch (PreviewImageSaveException) { return svgBytes; }
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

    private async Task<string> DownloadRemoteAsync(
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
            using HttpResponseMessage response = await CoreOperationTrace.MeasureAsync("ImageHttpHeaders", () => httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)).ConfigureAwait(false);
            CoreOperationTrace.Value("ImageHttpStatus", (long)response.StatusCode);

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
                CoreOperationTrace.Value("ImageHttpRedirect", redirect + 1);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PreviewImageSaveException(
                    $"远程图片下载失败（HTTP {(int)response.StatusCode}）。");
            }

            if (response.Content.Headers.ContentLength is <= 0 or > MaximumImageBytes)
                throw new PreviewImageSaveException("远程图片为空或超过 32 MB，无法保存。");
            string extension = ResponseExtension(response.Content.Headers.ContentType?.MediaType, source.Extension);
            await using Stream input = await CoreOperationTrace.MeasureAsync("ImageResponseStream",
                () => response.Content.ReadAsStreamAsync(cancellationToken)).ConfigureAwait(false);
            await CoreOperationTrace.MeasureAsync("ImageDownloadBody", () => CopyWithLimitAsync(input, destination, cancellationToken)).ConfigureAwait(false);
            return extension;
        }
        throw new PreviewImageSaveException("远程图片重定向次数过多。");
    }

    private async Task<(byte[] Bytes, string Extension)> MaterializeRemoteAsync(
        PreviewImageSaveSource source, CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        string extension;
        try
        {
            extension = await DownloadRemoteAsync(source, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new PreviewImageSaveException("远程图片下载失败，请检查网络连接。", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            CoreOperationTrace.Value("ImageHttpTimeout", (long)httpClient.Timeout.TotalMilliseconds);
            throw new PreviewImageSaveException("远程图片下载超时。", exception);
        }
        byte[] bytes = destination.ToArray();
        // Some badge/CDN endpoints omit a useful Content-Type as well as a suffix.
        // Sniff common image signatures only for naming; never reject SVG elements.
        extension = CoreOperationTrace.Measure("ImageFormatDetect", () => DetectImageExtension(bytes, extension));
        if (extension == ".svg") bytes = NormalizeSvgSize(bytes);
        return (bytes, extension);
    }

    private static string ResponseExtension(string? contentType, string fallback) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" or "image/x-ms-bmp" => ".bmp",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        _ => fallback,
    };

    private static string DetectImageExtension(byte[] bytes, string extension)
    {
        if (extension == ".img")
        {
            extension = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }) ? ".png"
                : bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? ".jpg"
                : bytes.AsSpan().StartsWith("GIF8"u8) ? ".gif"
                : bytes.AsSpan().StartsWith("BM"u8) ? ".bmp"
                : bytes.Length >= 12 && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? ".webp"
                : System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 16384))
                    .Contains("<svg", StringComparison.OrdinalIgnoreCase) ? ".svg"
                : throw new PreviewImageSaveException("服务器没有返回可识别的图片格式。");
        }
        return extension;
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        double readMilliseconds = 0, writeMilliseconds = 0;
        try
        {
            while (true)
            {
                long readStart = Stopwatch.GetTimestamp();
                int read;
                try { read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
                finally { readMilliseconds += Stopwatch.GetElapsedTime(readStart).TotalMilliseconds; }
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumImageBytes)
                {
                    throw new PreviewImageSaveException("预览图片超过 32 MB，无法保存。");
                }

                long writeStart = Stopwatch.GetTimestamp();
                try { await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); }
                finally { writeMilliseconds += Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds; }
            }

            if (total == 0)
            {
                throw new PreviewImageSaveException("预览图片为空，无法保存。");
            }
        }
        finally
        {
            CoreOperationTrace.Report("ImageStreamRead", readMilliseconds);
            CoreOperationTrace.Report("ImageStreamWrite", writeMilliseconds);
            CoreOperationTrace.Value("ImageBytes", total);
        }
    }

    private static string ValidateExtension(string extension)
    {
        if (!SupportedExtensions.Contains(extension))
        {
            throw new PreviewImageSaveException("仅支持保存 PNG、JPEG、GIF、BMP、WebP 和 SVG 图片。");
        }

        return extension.ToLowerInvariant();
    }

    private static string ValidateLocalExtension(string extension)
    {
        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            ? ".svg"
            : ValidateExtension(extension);
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
