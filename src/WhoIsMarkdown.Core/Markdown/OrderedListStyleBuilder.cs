using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Host-owned, fixed CSS only. Never accept author-provided CSS or inject extra
/// DOM nodes: native list-item counters preserve start/reversed/value semantics
/// and remain correct after incremental edits, including nested ordered lists.
/// </summary>
public static class OrderedListStyleBuilder
{
    public static string Build(OrderedListStyle style)
    {
        if (!Enum.IsDefined(style) || style == OrderedListStyle.Plain)
        {
            return string.Empty;
        }

        const string markerTypography = """
            .preview-document ol > li::marker {
              color: var(--wimd-accent, #4f51cf);
              font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
              font-weight: 700;
              font-variant-numeric: tabular-nums;
            }
            """;
        if (style == OrderedListStyle.Emphasis)
        {
            return markerTypography;
        }

        string radius = style == OrderedListStyle.Circle ? "999px" : "6px";
        return $$"""
            {{markerTypography}}
            .preview-document ol {
              padding-inline-start: 3em;
            }
            .preview-document ol > li {
              position: relative;
              min-height: 1.8em;
            }
            .preview-document ol > li::marker {
              content: "";
            }
            .preview-document ol > li::before {
              content: counter(list-item);
              position: absolute;
              inset-inline-end: calc(100% + 0.65em);
              top: 0.15em;
              box-sizing: border-box;
              width: max-content;
              min-width: 1.9em;
              padding: 0 0.35em;
              border: 1px solid var(--wimd-border, #dde2ec);
              border-radius: {{radius}};
              color: var(--wimd-accent, #4f51cf);
              background: var(--wimd-accent-soft, rgba(82, 84, 217, 0.12));
              font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif;
              font-size: 0.85em;
              font-weight: 600;
              font-variant-numeric: tabular-nums;
              line-height: 1.8;
              text-align: center;
              pointer-events: none;
            }
            """;
    }
}
