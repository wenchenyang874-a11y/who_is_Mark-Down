using System.Text.Json;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Builds a host-only script that replaces the preview body without navigating
/// WebView2. JSON serialization keeps rendered HTML data separate from script code.
/// </summary>
public sealed class PreviewUpdateScriptBuilder
{
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

              const root = document.scrollingElement || document.documentElement;
              const maximumY = Math.max(0, root.scrollHeight - root.clientHeight);
              window.scrollTo(previousX, Math.min(previousY, maximumY));
              return true;
            })();
            """;
    }
}
