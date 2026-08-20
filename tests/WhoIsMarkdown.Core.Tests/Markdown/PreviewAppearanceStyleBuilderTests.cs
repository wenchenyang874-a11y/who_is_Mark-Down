using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewAppearanceStyleBuilderTests
{
    [Fact]
    public void Build_WhenDarkThemeSelected_EmitsDarkVariablesAndFontSizes()
    {
        AppearanceSettings settings = new()
        {
            EditorFontFamily = "Consolas",
            PreviewFontFamily = "Microsoft YaHei UI",
            PreviewFontSize = 18,
        };

        string css = PreviewAppearanceStyleBuilder.Build(ApplicationTheme.Dark, settings);

        Assert.Contains("color-scheme: dark", css, StringComparison.Ordinal);
        Assert.Contains("--wimd-preview-font: \"Microsoft YaHei UI\"", css, StringComparison.Ordinal);
        Assert.Contains("--wimd-code-font: \"Consolas\"", css, StringComparison.Ordinal);
        Assert.Contains("--wimd-preview-font-size: 18px", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenFontContainsQuote_EscapesCssStringBoundary()
    {
        AppearanceSettings settings = new()
        {
            PreviewFontFamily = "Font\"; body { color: red; }",
        };

        string css = PreviewAppearanceStyleBuilder.Build(ApplicationTheme.Light, settings);

        Assert.Contains("Font\\\"; body", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--wimd-preview-font: \"Font\"; body", css, StringComparison.Ordinal);
    }
}
