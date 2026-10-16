using System.Security.Cryptography;
using System.Text;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>A complete, safe semantic block, identified only by the trusted host.</summary>
public sealed record MarkdownRenderBlock(string Key, string Html);

/// <summary>
/// Keeps the existing fragment API separate from the host-owned block containers.
/// Raw HTML cannot forge these containers: they are added after allowlist filtering.
/// Keys include the final image URLs, so directory/policy changes cannot reuse stale DOM.
/// </summary>
public sealed class MarkdownRenderResult
{
    private MarkdownRenderResult(IReadOnlyList<MarkdownRenderBlock> blocks)
    {
        Blocks = blocks;
        BodyHtml = string.Concat(blocks.Select(block => block.Html));
        StringBuilder preview = new();
        foreach (MarkdownRenderBlock block in blocks)
        {
            preview.Append("<div class=\"wimd-preview-block\" data-wimd-block=\"")
                .Append(block.Key).Append("\">").Append(block.Html).Append("</div>");
        }

        PreviewHtml = preview.ToString();
    }

    public IReadOnlyList<MarkdownRenderBlock> Blocks { get; }
    public string BodyHtml { get; }
    public string PreviewHtml { get; }

    public static MarkdownRenderResult Create(IEnumerable<string> safeBlocks)
    {
        ArgumentNullException.ThrowIfNull(safeBlocks);
        List<MarkdownRenderBlock> blocks = [];
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        foreach (string html in safeBlocks)
        {
            if (string.IsNullOrWhiteSpace(html)) continue;
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
            occurrences.TryGetValue(fingerprint, out int occurrence);
            occurrences[fingerprint] = occurrence + 1;
            blocks.Add(new MarkdownRenderBlock($"{fingerprint}-{occurrence}", html));
        }

        return new MarkdownRenderResult(blocks.AsReadOnly());
    }
}
