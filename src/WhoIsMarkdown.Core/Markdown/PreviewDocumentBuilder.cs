using System.Net;
using System.Text;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Wraps a trusted renderer fragment in a complete, offline-first preview page.
/// The content security policy allows only app-mapped document images and blocks
/// remote assets, page scripts, frames, forms, and plugins.
/// </summary>
public sealed class PreviewDocumentBuilder : IPreviewDocumentBuilder
{


    private const string EmptyDocumentMarkup = """
        <section class="preview-empty-state" aria-label="空文档提示">
          <div class="preview-empty-mark">M↓</div>
          <h1>开始写点什么吧</h1>
          <p>在编辑区输入 Markdown，格式化结果会实时显示在这里。</p>
        </section>
        """;

    public string Build(
        string bodyHtml,
        string styleSheet,
        RemoteImagePolicy? remoteImagePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        ArgumentNullException.ThrowIfNull(styleSheet);
        string visibleBody = GetVisibleBody(bodyHtml);
        string contentSecurityPolicy = CreateContentSecurityPolicy(
            remoteImagePolicy ?? RemoteImagePolicy.BlockAll);

        StringBuilder html = new(capacity: visibleBody.Length + styleSheet.Length + 640);
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"zh-CN\">");
        html.AppendLine("<head>");
        html.Append("  <meta http-equiv=\"Content-Security-Policy\" content=\"")
            .Append(WebUtility.HtmlEncode(contentSecurityPolicy))
            .AppendLine("\">");
        html.AppendLine("  <meta charset=\"utf-8\">");
        html.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("  <style>");
        html.AppendLine(styleSheet);
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("  <main class=\"preview-document\">");
        html.AppendLine(visibleBody);
        html.AppendLine("  </main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    private static string CreateContentSecurityPolicy(RemoteImagePolicy remoteImagePolicy)
    {
        IReadOnlyList<string> remoteSources = remoteImagePolicy.GetContentSecurityPolicySources();
        string imageSources = remoteSources.Count == 0
            ? "data: https://wimd-document.invalid"
            : $"data: https://wimd-document.invalid {string.Join(' ', remoteSources)}";
        return "default-src 'none'; " +
            "base-uri 'none'; " +
            "form-action 'none'; " +
            "frame-src 'none'; " +
            "object-src 'none'; " +
            "script-src 'none'; " +
            $"img-src {imageSources}; " +
            "style-src 'unsafe-inline'; " +
            "font-src data:;";
    }
    public string GetVisibleBody(string bodyHtml)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        return string.IsNullOrWhiteSpace(bodyHtml) ? EmptyDocumentMarkup : bodyHtml;
    }
}
