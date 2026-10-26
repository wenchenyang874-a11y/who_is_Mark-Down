using System.Text.Json;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class OrderedListStyleTests
{
    [Theory]
    [InlineData(OrderedListStyle.Plain)]
    [InlineData(OrderedListStyle.Emphasis)]
    [InlineData(OrderedListStyle.Circle)]
    [InlineData(OrderedListStyle.RoundedSquare)]
    public void Normalize_ValidStyle_PreservesChoice(OrderedListStyle style)
    {
        ApplicationSettings settings = new() { PreviewStyles = new() { OrderedList = style } };
        Assert.Equal(style, settings.Normalize().PreviewStyles.OrderedList);
    }

    [Fact]
    public void Normalize_OldOrInvalidSettings_FallsBackToOriginalAppearance()
    {
        Assert.Equal(OrderedListStyle.Plain, JsonSerializer.Deserialize<ApplicationSettings>("{}")!.Normalize().PreviewStyles.OrderedList);
        Assert.Equal(OrderedListStyle.Plain, new ApplicationSettings { PreviewStyles = new() { OrderedList = (OrderedListStyle)999 } }.Normalize().PreviewStyles.OrderedList);
        Assert.Equal(OrderedListStyle.Plain, JsonSerializer.Deserialize<ApplicationSettings>("{\"PreviewStyles\":null}")!.Normalize().PreviewStyles.OrderedList);
    }

    [Theory]
    [InlineData(OrderedListStyle.Plain)]
    [InlineData(OrderedListStyle.Emphasis)]
    [InlineData(OrderedListStyle.Circle)]
    [InlineData(OrderedListStyle.RoundedSquare)]
    public void SaveAndReload_GlobalStyle_PersistsAcrossStoreInstances(OrderedListStyle style)
    {
        string directory = Path.Combine(Path.GetTempPath(), "wimd-list-style-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            JsonApplicationSettingsStore first = new(path);
            first.Save(new ApplicationSettings { PreviewStyles = new() { OrderedList = style } });
            Assert.Equal(style, new JsonApplicationSettingsStore(path).Load().PreviewStyles.OrderedList);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Merge_StaleWindowSavesOtherPreference_DoesNotOverwriteGlobalStyle()
    {
        ApplicationSettings before = new();
        ApplicationSettings disk = before with { PreviewStyles = new() { OrderedList = OrderedListStyle.Circle } };
        ApplicationSettings after = before with { BackgroundOpacity = 0.6 };

        ApplicationSettings result = ApplicationSettingsChanges.Merge(disk, before, after);

        Assert.Equal(OrderedListStyle.Circle, result.PreviewStyles.OrderedList);
        Assert.Equal(0.6, result.BackgroundOpacity);
    }

    [Fact]
    public void Merge_ExplicitStyleChange_ChangesOnlyRequestedField()
    {
        ApplicationSettings before = new();
        ApplicationSettings disk = before with { Appearance = new() { Theme = ApplicationTheme.Dark } };
        ApplicationSettings after = before with { PreviewStyles = new() { OrderedList = OrderedListStyle.Emphasis } };

        ApplicationSettings result = ApplicationSettingsChanges.Merge(disk, before, after);

        Assert.Equal(OrderedListStyle.Emphasis, result.PreviewStyles.OrderedList);
        Assert.Equal(ApplicationTheme.Dark, result.Appearance.Theme);
    }

    [Theory]
    [InlineData(OrderedListStyle.Plain)]
    [InlineData((OrderedListStyle)999)]
    public void Build_PlainOrInvalidStyle_DoesNotOverrideDocumentMarkers(OrderedListStyle style)
    {
        Assert.Empty(OrderedListStyleBuilder.Build(style));
    }

    [Fact]
    public void Build_Emphasis_ChangesTypographyWithoutReplacingNativeCounter()
    {
        string css = OrderedListStyleBuilder.Build(OrderedListStyle.Emphasis);

        Assert.Contains("ol > li::marker", css, StringComparison.Ordinal);
        Assert.Contains("font-weight: 700", css, StringComparison.Ordinal);
        Assert.Contains("var(--wimd-accent", css, StringComparison.Ordinal);
        Assert.DoesNotContain("::before", css, StringComparison.Ordinal);
        Assert.DoesNotContain("counter-reset", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrderedListStyle.Circle, "999px")]
    [InlineData(OrderedListStyle.RoundedSquare, "6px")]
    public void Build_Badges_UsesNativeCounterAndExpandableThemeAwareDecoration(OrderedListStyle style, string radius)
    {
        string css = OrderedListStyleBuilder.Build(style);

        Assert.Contains("content: counter(list-item)", css, StringComparison.Ordinal);
        Assert.Contains("width: max-content", css, StringComparison.Ordinal);
        Assert.Contains("inset-inline-end:", css, StringComparison.Ordinal);
        Assert.Contains($"border-radius: {radius}", css, StringComparison.Ordinal);
        Assert.Contains("var(--wimd-accent-soft", css, StringComparison.Ordinal);
        Assert.DoesNotContain("counter-reset", css, StringComparison.Ordinal);
        Assert.DoesNotContain("counter-increment", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Warm)]
    public void BuildAppearance_ThemeWithBadge_CombinesPaletteAndListRules(ApplicationTheme theme)
    {
        string css = PreviewAppearanceStyleBuilder.Build(theme, new(), new() { OrderedList = OrderedListStyle.Circle });
        Assert.Contains("--wimd-accent-soft:", css, StringComparison.Ordinal);
        Assert.Contains("content: counter(list-item)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ListWithStartNestingTaskAndFollowingParagraph_PreservesSourceSemantics()
    {
        const string markdown = "7. 第一项\n8. 第二项\n   1. 嵌套\n   2. [x] 任务\n\n正文";
        string html = new MarkdownRenderer().RenderBody(markdown);
        Assert.Contains("start=\"7\"", html, StringComparison.Ordinal);
        Assert.Equal(2, new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).QuerySelectorAll("ol").Length);
        Assert.Contains("checkbox", html, StringComparison.Ordinal);
        Assert.Contains("正文", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wimd-list", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WindowOverrideOrFollowGlobal_DoesNotMutatePersistedDefaults()
    {
        PreviewStyleSettings defaults = new() { OrderedList = OrderedListStyle.Circle };
        PreviewStyleOverrides local = new() { OrderedList = OrderedListStyle.Emphasis };
        Assert.Equal(OrderedListStyle.Emphasis, local.Resolve(defaults).OrderedList);
        Assert.Equal(OrderedListStyle.Circle, new PreviewStyleOverrides().Resolve(defaults).OrderedList);
        Assert.Equal(OrderedListStyle.Circle, (local with { OrderedList = (OrderedListStyle)999 }).Resolve(defaults).OrderedList);
        Assert.Equal(OrderedListStyle.Circle, defaults.OrderedList);
        Assert.Equal(OrderedListStyle.Emphasis, local.OrderedList);
        Assert.Equal(OrderedListStyle.RoundedSquare,
            new PreviewStyleOverrides().Resolve(defaults with { OrderedList = OrderedListStyle.RoundedSquare }).OrderedList);
    }
}
