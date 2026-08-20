using System.Globalization;
using System.Text;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Produces host-owned CSS variables for the preview. User-selected font names
/// are encoded as CSS string literals so they can never escape into a rule.
/// </summary>
public static class PreviewAppearanceStyleBuilder
{
    public static string Build(ApplicationTheme effectiveTheme, AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppearanceSettings normalized = settings.Normalize();
        PreviewPalette palette = GetPalette(effectiveTheme);
        string bodyFont = CreateFontStack(
            normalized.PreviewFontFamily,
            "\"Segoe UI Variable Text\", \"Segoe UI\", \"Microsoft YaHei UI\", sans-serif");
        string codeFont = CreateFontStack(
            normalized.EditorFontFamily,
            "\"Cascadia Mono\", Consolas, monospace");
        string size = normalized.PreviewFontSize.ToString("0.##", CultureInfo.InvariantCulture);

        return $$"""
            :root {
              color-scheme: {{palette.ColorScheme}};
              --wimd-preview-font: {{bodyFont}};
              --wimd-code-font: {{codeFont}};
              --wimd-preview-font-size: {{size}}px;
              --wimd-text: {{palette.Text}};
              --wimd-heading: {{palette.Heading}};
              --wimd-secondary: {{palette.Secondary}};
              --wimd-accent: {{palette.Accent}};
              --wimd-accent-soft: {{palette.AccentSoft}};
              --wimd-document: {{palette.Document}};
              --wimd-border: {{palette.Border}};
              --wimd-heading-border: {{palette.HeadingBorder}};
              --wimd-quote: {{palette.Quote}};
              --wimd-quote-bg: {{palette.QuoteBackground}};
              --wimd-inline-code: {{palette.InlineCode}};
              --wimd-inline-code-bg: {{palette.InlineCodeBackground}};
              --wimd-code-bg: {{palette.CodeBackground}};
              --wimd-table-bg: {{palette.TableBackground}};
              --wimd-table-cell-bg: {{palette.TableCellBackground}};
              --wimd-table-heading-bg: {{palette.TableHeadingBackground}};
              --wimd-control-bg: {{palette.ControlBackground}};
              --wimd-control-border: {{palette.ControlBorder}};
              --wimd-scroll-thumb: {{palette.ScrollThumb}};
            }
            """;
    }

    private static string CreateFontStack(string? family, string fallback)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return fallback;
        }

        StringBuilder encoded = new(family.Length + 2);
        encoded.Append('"');
        foreach (char character in family)
        {
            if (character is '"' or '\\')
            {
                encoded.Append('\\');
            }

            encoded.Append(character);
        }

        encoded.Append("\", ").Append(fallback);
        return encoded.ToString();
    }

    private static PreviewPalette GetPalette(ApplicationTheme theme)
    {
        return theme switch
        {
            ApplicationTheme.Dark => new(
                "dark", "#E7EAF2", "#F4F6FB", "#AAB1C2", "#A8ACFF",
                "rgba(116, 103, 225, 0.18)", "rgba(24, 29, 40, 0.72)",
                "rgba(112, 123, 148, 0.64)", "#3C4559", "#BDC3D2",
                "rgba(103, 90, 193, 0.2)", "#CDD0FF", "#2B3146",
                "rgba(29, 35, 49, 0.86)", "rgba(22, 27, 38, 0.38)",
                "rgba(32, 38, 52, 0.5)", "rgba(52, 60, 78, 0.86)",
                "rgba(37, 43, 58, 0.96)", "#59647A", "rgba(168, 174, 195, 0.62)"),
            ApplicationTheme.Warm => new(
                "light", "#40372E", "#2E261F", "#746657", "#93623D",
                "rgba(168, 111, 69, 0.13)", "rgba(255, 249, 237, 0.46)",
                "rgba(187, 159, 120, 0.7)", "#E5D4BA", "#6F5F50",
                "rgba(235, 217, 188, 0.62)", "#704B31", "#F1E5D3",
                "rgba(244, 232, 211, 0.82)", "rgba(255, 250, 240, 0.32)",
                "rgba(255, 251, 243, 0.3)", "rgba(231, 214, 188, 0.78)",
                "rgba(255, 252, 246, 0.94)", "#B79B78", "rgba(117, 96, 73, 0.58)"),
            _ => new(
                "light", "#20283A", "#151C2D", "#60687A", "#4F51CF",
                "rgba(82, 84, 217, 0.12)", "rgba(255, 255, 255, 0.25)",
                "rgba(221, 226, 236, 0.92)", "#E5E8EF", "#60687A", "#F5F5FC",
                "#34368F", "#EFF0FA", "rgba(238, 241, 247, 0.72)",
                "rgba(255, 255, 255, 0.3)", "rgba(255, 255, 255, 0.22)",
                "rgba(230, 234, 243, 0.78)", "rgba(255, 255, 255, 0.94)",
                "#D9DDEA", "rgba(89, 97, 117, 0.58)"),
        };
    }

    private sealed record PreviewPalette(
        string ColorScheme,
        string Text,
        string Heading,
        string Secondary,
        string Accent,
        string AccentSoft,
        string Document,
        string Border,
        string HeadingBorder,
        string Quote,
        string QuoteBackground,
        string InlineCode,
        string InlineCodeBackground,
        string CodeBackground,
        string TableBackground,
        string TableCellBackground,
        string TableHeadingBackground,
        string ControlBackground,
        string ControlBorder,
        string ScrollThumb);
}
