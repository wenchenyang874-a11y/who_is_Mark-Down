using System.Net;
using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Resolves Markdown image paths against the current document directory and
/// converts local paths to a WebView2 virtual-host URL. Web images, SVG and data
/// images are passed through without extension-based or SVG-content filtering.
/// </summary>
public sealed partial class LocalImageUrlResolver : ILocalImageUrlResolver
{
    public const string VirtualHostName = "wimd-document.invalid";

    private const string InertPixel =
        "data:image/gif;base64,R0lGODlhAQABAAAAACw=";

    public string RewriteGeneratedHtml(string bodyHtml, string? documentPath,
        RemoteImagePolicy? remoteImagePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        if (bodyHtml.Length == 0)
        {
            return bodyHtml;
        }

        string? documentDirectory = GetDocumentDirectory(documentPath);
        return ImageSourceRegex().Replace(
            bodyHtml,
            match => string.Concat(
                match.Groups[1].Value,
                RewriteSource(
                    match.Groups[2].Value,
                    documentDirectory,
                    remoteImagePolicy ?? RemoteImagePolicy.TrustAll),
                match.Groups[3].Value));
    }

    private static string RewriteSource(
        string encodedSource,
        string? documentDirectory,
        RemoteImagePolicy remoteImagePolicy)
    {
        string source = WebUtility.HtmlDecode(encodedSource);
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return encodedSource;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return InertPixel;
        }

        if (source.StartsWith("//", StringComparison.Ordinal)) source = "https:" + source;
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? absoluteUri)
            && !absoluteUri.IsFile)
        {
            return remoteImagePolicy.Allows(absoluteUri)
                ? WebUtility.HtmlEncode(absoluteUri.AbsoluteUri)
                : InertPixel;
        }

        if (documentDirectory is null)
        {
            return InertPixel;
        }

        try
        {
            string pathPart = RemoveQueryAndFragment(Uri.UnescapeDataString(source.Trim()));
            string candidate = Path.IsPathFullyQualified(pathPart)
                ? Path.GetFullPath(pathPart)
                : Path.GetFullPath(pathPart, documentDirectory);

            if (!IsWithinDirectory(candidate, documentDirectory))
            {
                return InertPixel;
            }

            string relativePath = Path.GetRelativePath(documentDirectory, candidate);
            string escapedPath = string.Join(
                '/',
                relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString));
            // SVG uses Chromium's native image mode, exactly like raster images;
            // no application sanitizer rewrites or rejects the SVG document.
            return $"https://{VirtualHostName}/{escapedPath}";
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return InertPixel;
        }
    }

    private static string? GetDocumentDirectory(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(documentPath);
            return Path.GetDirectoryName(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWithinDirectory(string candidate, string directory)
    {
        string relativePath = Path.GetRelativePath(directory, candidate);
        return !Path.IsPathFullyQualified(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            && !relativePath.StartsWith(
                string.Concat("..", Path.AltDirectorySeparatorChar),
                StringComparison.Ordinal);
    }

    private static string RemoveQueryAndFragment(string source)
    {
        int delimiter = source.IndexOfAny(['?', '#']);
        return delimiter >= 0 ? source[..delimiter] : source;
    }

    [GeneratedRegex(
        "(<img\\b[^>]*\\bsrc=\")([^\"]*)(\")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex ImageSourceRegex();
}
