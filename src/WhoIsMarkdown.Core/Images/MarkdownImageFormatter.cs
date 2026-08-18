namespace WhoIsMarkdown.Core.Images;

public static class MarkdownImageFormatter
{
    public static string CreateRemote(string altText, Uri imageUrl)
    {
        ArgumentNullException.ThrowIfNull(imageUrl);
        if (!imageUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("图床图片必须使用 HTTPS 地址。", nameof(imageUrl));
        }

        return Create(altText, imageUrl.AbsoluteUri);
    }

    public static string CreateLocal(string altText, string markdownPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        string escapedPath = string.Join(
            '/',
            markdownPath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return Create(altText, escapedPath);
    }

    private static string Create(string altText, string destination)
    {
        string safeAltText = (altText ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return $"![{safeAltText}]({destination})";
    }
}
