using System.Text.Json;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Builds a host-only script that replaces the preview body without navigating
/// WebView2. JSON serialization keeps rendered HTML data separate from script code.
/// </summary>
public sealed class PreviewUpdateScriptBuilder
{
    /// <summary>
    /// Patches host-owned semantic blocks. Acknowledging the expected base before
    /// mutating prevents a stale host plan from silently removing newer page data.
    /// Unchanged nodes retain generated diagrams, image decoders and copy controls.
    /// </summary>
    public static string BuildPatch(
        IReadOnlyList<MarkdownRenderBlock> previous,
        IReadOnlyList<MarkdownRenderBlock> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        Dictionary<string, string> prior = previous.ToDictionary(block => block.Key, block => block.Html, StringComparer.Ordinal);
        string expected = JsonSerializer.Serialize(previous.Select(block => block.Key));
        string desired = JsonSerializer.Serialize(current.Select(block => block.Key));
        Dictionary<string, string> changed = new(StringComparer.Ordinal);
        foreach (MarkdownRenderBlock block in current)
        {
            if (!prior.TryGetValue(block.Key, out string? html) || !string.Equals(html, block.Html, StringComparison.Ordinal))
            {
                changed.Add(block.Key, block.Html);
            }
        }

        string replacements = JsonSerializer.Serialize(changed);
        return $$"""
            (() => {
              const preview = document.querySelector('main.preview-document');
              if (!preview) return false;
              const expected = {{expected}};
              const desired = {{desired}};
              const changed = {{replacements}};
              const existing = [...preview.children];
              if (existing.length !== expected.length
                  || existing.some((node, index) => node.dataset.wimdBlock !== expected[index])) return false;
              const nodes = new Map(existing.map(node => [node.dataset.wimdBlock, node]));
              // Prepare everything before mutation. Author HTML is already filtered;
              // JSON keeps HTML closing tags separate from executable host script.
              for (const [key, html] of Object.entries(changed)) {
                const node = document.createElement('div');
                node.className = 'wimd-preview-block';
                node.dataset.wimdBlock = key;
                node.innerHTML = html;
                nodes.set(key, node);
              }
              if (new Set(desired).size !== desired.length || desired.some(key => !nodes.has(key))) return false;
              // Mermaid's per-document limit is enforced against a fresh set of
              // sources. Near that limit, or after an error, reuse could preserve
              // an old overflow decision after diagrams were inserted or removed.
              let diagramCount = 0;
              for (const key of desired) {
                const node = nodes.get(key);
                if (node.querySelector('.wimd-mermaid-error')) return false;
                diagramCount += node.querySelectorAll('pre.mermaid, pre > code.language-mermaid, figure.wimd-mermaid-diagram').length;
                if (diagramCount >= 64) return false;
              }
              const previousX = window.scrollX;
              const previousY = window.scrollY;
              const keep = new Set(desired.map(key => nodes.get(key)));
              existing.forEach(node => { if (!keep.has(node)) node.remove(); });
              let cursor = preview.firstElementChild;
              for (const key of desired) {
                const node = nodes.get(key);
                if (node === cursor) cursor = cursor.nextElementSibling;
                else preview.insertBefore(node, cursor);
              }
              document.dispatchEvent(new CustomEvent('wimd:preview-updated'));
              const root = document.scrollingElement || document.documentElement;
              window.scrollTo(previousX, Math.min(previousY, Math.max(0, root.scrollHeight - root.clientHeight)));
              return true;
            })();
            """;
    }

    public static string Build(string bodyHtml)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        string encodedBody = JsonSerializer.Serialize(bodyHtml);

        return $$"""
            (() => {
              const preview = document.querySelector('main.preview-document');
              if (!preview) return false;

              const previousX = window.scrollX;
              const previousY = window.scrollY;
              const template = document.createElement('template');
              template.innerHTML = {{encodedBody}};
              preview.replaceChildren(template.content);
              document.dispatchEvent(new CustomEvent('wimd:preview-updated'));

              const root = document.scrollingElement || document.documentElement;
              const maximumY = Math.max(0, root.scrollHeight - root.clientHeight);
              window.scrollTo(previousX, Math.min(previousY, maximumY));
              return true;
            })();
            """;
    }
}
