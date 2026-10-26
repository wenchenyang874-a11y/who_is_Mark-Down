using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ThemeAndBundledFontTests
{
    public static TheoryData<ApplicationTheme> Themes => new(Enum.GetValues<ApplicationTheme>());

    [Fact]
    public void 主题目录_保留旧编号并覆盖所有选项()
    {
        Assert.Equal(0, (int)ApplicationTheme.System);
        Assert.Equal(1, (int)ApplicationTheme.Light);
        Assert.Equal(2, (int)ApplicationTheme.Dark);
        Assert.Equal(3, (int)ApplicationTheme.Warm);
        Assert.Equal(Enum.GetValues<ApplicationTheme>().Length, ThemeCatalog.Options.Count);
        Assert.Equal(ThemeCatalog.Options.Count, ThemeCatalog.Options.DistinctBy(t => t.Theme).Count());
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void 主题归一化_所有已知主题均保留并有完整样式(ApplicationTheme theme)
    {
        Assert.Equal(theme, new AppearanceSettings { Theme = theme }.Normalize().Theme);
        string css = PreviewAppearanceStyleBuilder.Build(theme, new());
        Assert.Contains("--wimd-table-heading-bg:", css, StringComparison.Ordinal);
        Assert.Contains("--wimd-code-bg:", css, StringComparison.Ordinal);
        if (ThemeCatalog.HasExtendedPalette(theme))
        {
            ThemeColors colors = ThemeCatalog.Get(theme).Colors;
            Assert.Contains($"--wimd-accent: {colors.Accent}", css, StringComparison.Ordinal);
            Assert.Contains($"color-scheme: {(colors.IsDark ? "dark" : "light")}", css, StringComparison.Ordinal);
            Assert.True(Contrast(colors.Text, colors.Surface) >= 7, "正文对比度不足。");
            Assert.True(Contrast(colors.Secondary, colors.Surface) >= 4.5, "次要文字对比度不足。");
            Assert.True(Contrast(colors.Accent, colors.Surface) >= 4.5, "强调文字对比度不足。");
        }
    }

    [Fact]
    public void 内置字体_仅固定目录与固定标识进入样式()
    {
        Assert.Equal(5, BundledFontCatalog.Fonts.Count);
        Assert.Equal(3, BundledFontCatalog.Fonts.Count(f => f.IsChinese));
        foreach (BundledFont font in BundledFontCatalog.Fonts)
        {
            Assert.DoesNotContain("..", font.RelativePath, StringComparison.Ordinal);
            string css = PreviewAppearanceStyleBuilder.Build(ApplicationTheme.Rose,
                new() { EditorFontFamily = font.Id, PreviewFontFamily = font.Id });
            Assert.Contains($"https://wimd-fonts.invalid/{font.RelativePath}", css, StringComparison.Ordinal);
            Assert.Equal(1, css.Split("@font-face", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("base64,", css, StringComparison.Ordinal);
            Assert.Equal(font.Id, new AppearanceSettings { PreviewFontFamily = font.Id }.Normalize().PreviewFontFamily);
        }
        Assert.Null(new AppearanceSettings { PreviewFontFamily = "wimd:../../bad" }.Normalize().PreviewFontFamily);
        Assert.Null(new AppearanceSettings { PreviewFontFamily = "WIMD:unknown" }.Normalize().PreviewFontFamily);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void 外观持久化_新旧主题与内置字体标识往返一致(ApplicationTheme theme)
    {
        using TemporaryDirectory directory = new();
        JsonApplicationSettingsStore store = new(Path.Combine(directory.Path, "settings.json"));
        store.Save(new() { Appearance = new() { Theme = theme, EditorFontFamily = "wimd:fira-code", PreviewFontFamily = "wimd:lxgw-wenkai" } });
        ApplicationSettings result = store.Load();
        Assert.Equal(theme, result.Appearance.Theme);
        Assert.Equal("wimd:fira-code", result.Appearance.EditorFontFamily);
        Assert.Equal("wimd:lxgw-wenkai", result.Appearance.PreviewFontFamily);
    }

    [Fact]
    public void 预览策略_只额外允许安装包内置字体虚拟域()
    {
        string html = new PreviewDocumentBuilder().Build("<p>test</p>", "");
        Assert.Contains("font-src data: https://wimd-fonts.invalid;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("font-src https:;", html, StringComparison.Ordinal);
        Assert.Contains("script-src &#39;none&#39;", html, StringComparison.Ordinal);
    }

    private static double Contrast(string left, string right)
    {
        static double Light(string hex)
        {
            double[] rgb = Enumerable.Range(0, 3).Select(i => Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255d)
                .Select(c => c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4)).ToArray();
            return .2126 * rgb[0] + .7152 * rgb[1] + .0722 * rgb[2];
        }
        double a = Light(left), b = Light(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
