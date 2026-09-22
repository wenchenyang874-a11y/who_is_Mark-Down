using System.Net;
using System.Text;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Produces the save-as formats offered for a preview image. Every format revalidates
/// the prepared cache file first, because those bytes can be replaced between the
/// preview and the save, and every format lands on disk through the shared atomic
/// replacement so a failure never leaves a half-written target.
/// </summary>
public sealed class PreviewImageExportService
{
    /// <summary>
    /// Upper bound for a rasterized export. A bitmap is captured from an offscreen
    /// render surface, and an unbounded diagram would ask for a window larger than the
    /// desktop can host, so oversized vector content is scaled down proportionally.
    /// </summary>
    public const int MaximumRasterDimension = 4096;

    private readonly PreviewImageSaveService saveService;

    public PreviewImageExportService(PreviewImageSaveService saveService)
    {
        this.saveService = saveService ?? throw new ArgumentNullException(nameof(saveService));
    }

    /// <summary>
    /// Formats that make sense for this picture. Entries that would just repeat the
    /// current encoding are omitted, so a PNG source is not offered "PNG" twice.
    /// </summary>
    public static IReadOnlyList<PreviewImageExportFormat> GetAvailableFormats(
        PreparedPreviewImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetAvailableFormats(image.Extension);
    }

    /// <summary>
    /// Format list for a preview image that has not been materialized yet. The live
    /// preview only knows the picture's URL when it builds its context menu, so the
    /// same rule has to be available from the encoding alone.
    /// </summary>
    public static IReadOnlyList<PreviewImageExportFormat> GetAvailableFormats(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        List<PreviewImageExportFormat> formats = [PreviewImageExportFormat.Original];
        // A vector source always benefits from a rasterized copy, and a bitmap that is
        // already PNG would only be offered its own encoding twice.
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            formats.Add(PreviewImageExportFormat.Png);
        }

        formats.Add(PreviewImageExportFormat.Html);
        return formats;
    }

    public static string GetDisplayName(
        PreparedPreviewImage image,
        PreviewImageExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetDisplayName(image.Extension, image.IsSvg, format);
    }

    /// <summary>
    /// Label for one menu entry. A vector source is named by what it actually is
    /// instead of the generic "original", which is what the user is choosing between.
    /// </summary>
    public static string GetDisplayName(
        string extension,
        bool isSvg,
        PreviewImageExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return format switch
        {
            PreviewImageExportFormat.Original => isSvg
                ? "SVG 矢量图（.svg）"
                : $"原格式（{extension}）",
            PreviewImageExportFormat.Png => "PNG 图片（.png）",
            PreviewImageExportFormat.Html => "HTML 网页（.html）",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    public static string GetSuggestedFileName(
        PreparedPreviewImage image,
        PreviewImageExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetSuggestedFileName(
            image.SuggestedFileName,
            image.Extension,
            image.IsSvg,
            format);
    }

    public static string GetSuggestedFileName(
        string suggestedFileName,
        string extension,
        bool isSvg,
        PreviewImageExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        return string.Concat(
            Path.GetFileNameWithoutExtension(suggestedFileName),
            GetExtension(extension, isSvg, format));
    }

    public static string GetExtension(
        PreparedPreviewImage image,
        PreviewImageExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        return GetExtension(image.Extension, image.IsSvg, format);
    }

    public static string GetExtension(
        string extension,
        bool isSvg,
        PreviewImageExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return format switch
        {
            PreviewImageExportFormat.Original => isSvg ? ".svg" : extension,
            PreviewImageExportFormat.Png => ".png",
            PreviewImageExportFormat.Html => ".html",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    /// <summary>
    /// Writes one export format to the user's destination.
    /// </summary>
    /// <param name="rasterizer">
    /// Required only for <see cref="PreviewImageExportFormat.Png"/>; the other formats
    /// either copy validated bytes or build a page in memory.
    /// </param>
    public async Task<bool> ExportAsync(
        PreparedPreviewImage image,
        PreviewImageExportFormat format,
        string targetPath,
        IPreviewImageRasterizer? rasterizer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (format == PreviewImageExportFormat.Original)
        {
            // The original encoding already owns the atomic copy, the extension match
            // check and the SVG re-sanitization.
            return await saveService.SavePreparedAsync(image, targetPath, cancellationToken)
                .ConfigureAwait(false);
        }

        (string preparedPath, _) = await saveService
            .ValidatePreparedAsync(image, cancellationToken)
            .ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(preparedPath, cancellationToken)
            .ConfigureAwait(false);

        if (format == PreviewImageExportFormat.Html)
        {
            // Wrapping needs no renderer, so a caller that only builds the standalone
            // page is not forced to provide one.
            return await saveService.SaveBytesAsync(
                BuildStandaloneHtml(bytes, image),
                ".html",
                targetPath,
                cancellationToken).ConfigureAwait(false);
        }

        if (format != PreviewImageExportFormat.Png)
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        ArgumentNullException.ThrowIfNull(rasterizer);
        return await saveService.SaveBytesAsync(
            await RasterizeAsync(bytes, image, rasterizer, cancellationToken)
                .ConfigureAwait(false),
            ".png",
            targetPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Produces the PNG payload. A bitmap source is handed over untouched so the
    /// renderer can use its own intrinsic size; a vector source is normalized first so
    /// it has a real pixel size to rasterize against.
    /// </summary>
    private static async Task<byte[]> RasterizeAsync(
        byte[] bytes,
        PreparedPreviewImage image,
        IPreviewImageRasterizer rasterizer,
        CancellationToken cancellationToken)
    {
        if (!image.IsSvg)
        {
            return await rasterizer.RasterizeToPngAsync(
                bytes,
                string.Concat("image", image.Extension),
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        (byte[] normalized, int width, int height) = SvgPixelSizeNormalizer.Apply(bytes);
        (int rasterWidth, int rasterHeight) = ScaleToRasterLimit(width, height);
        return await rasterizer.RasterizeToPngAsync(
            normalized,
            "image.svg",
            rasterWidth,
            rasterHeight,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps the image in a self-contained page. The picture travels as base64 so the
    /// saved file has no companion assets and no network reference, which keeps the
    /// export usable offline and keeps remote tracking out of an archived page.
    /// </summary>
    internal static byte[] BuildStandaloneHtml(byte[] bytes, PreparedPreviewImage image)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(image);
        string mediaType = ResolveMediaType(image.Extension);
        string source = string.Concat(
            "data:",
            mediaType,
            ";base64,",
            Convert.ToBase64String(bytes));
        string title = WebUtility.HtmlEncode(
            Path.GetFileNameWithoutExtension(image.SuggestedFileName));
        return Encoding.UTF8.GetBytes($$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline';">
              <title>{{title}}</title>
              <style>
                html, body { margin: 0; min-height: 100%; }
                body {
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  padding: 24px;
                  box-sizing: border-box;
                  background: #ffffff;
                }
                img { max-width: 100%; height: auto; }
              </style>
            </head>
            <body><img src="{{source}}" alt="{{title}}"></body>
            </html>
            """);
    }

    private static (int Width, int Height) ScaleToRasterLimit(double width, double height)
    {
        double longestSide = Math.Max(width, height);
        double scale = longestSide > MaximumRasterDimension
            ? MaximumRasterDimension / longestSide
            : 1;
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static string ResolveMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
