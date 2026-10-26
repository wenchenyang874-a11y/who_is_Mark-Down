namespace WhoIsMarkdown.Core.Settings;

/// <summary>Plain color tokens shared by shell swatches and preview CSS; no WPF dependency.</summary>
public sealed record ThemeColors(
    string Background, string Surface, string Muted, string Text, string Heading,
    string Secondary, string Accent, string AccentHover, string Border, bool IsDark = false);

public sealed record ThemeOption(ApplicationTheme Theme, string Name, string Description, ThemeColors Colors);

public static class ThemeCatalog
{
    // Append themes rather than reordering enum values persisted by older builds.
    public static IReadOnlyList<ThemeOption> Options { get; } = Array.AsReadOnly<ThemeOption>(
    [
        new(ApplicationTheme.System, "跟随 Windows", "随系统切换明暗", new("#F4F5FA", "#FFFFFF", "#F7F8FC", "#182033", "#151C2D", "#687086", "#5B5CE2", "#4B4CCB", "#DDE2EC")),
        new(ApplicationTheme.Light, "明亮", "清晰、纯净的日常基底", new("#F4F5FA", "#FFFFFF", "#F7F8FC", "#182033", "#151C2D", "#687086", "#5B5CE2", "#4B4CCB", "#DDE2EC")),
        new(ApplicationTheme.Dark, "深色", "沉静的石墨灰", new("#11151D", "#262C39", "#2C3342", "#EDF0F7", "#F4F6FB", "#A9B0C0", "#898BFF", "#A6A8FF", "#586276", true)),
        new(ApplicationTheme.Warm, "暖色护眼", "温润的纸张质感", new("#F4EBDD", "#FFF9EF", "#EFE2D0", "#352B23", "#2E261F", "#766858", "#9A6640", "#7D4F30", "#D9C5A8")),
        new(ApplicationTheme.Rose, "樱雾粉", "轻盈雾粉 · 柔和玫瑰", new("#FBF4F6", "#FFFAFB", "#F4E9EE", "#40323A", "#352A31", "#77616D", "#9B526F", "#83405B", "#DCC5D0")),
        new(ApplicationTheme.Sage, "青苔", "灰调浅绿 · 自然安静", new("#F1F5F1", "#FAFCF9", "#E5EDE4", "#2D3D34", "#24332B", "#5E7165", "#476E5B", "#365745", "#C6D5C8")),
        new(ApplicationTheme.Mist, "雾屿蓝", "清透蓝灰 · 开阔留白", new("#F1F5F9", "#FAFCFE", "#E5ECF4", "#2D3A49", "#233141", "#5F7085", "#496E91", "#385773", "#C5D3E2")),
        new(ApplicationTheme.Lilac, "鸢尾紫", "淡雅灰紫 · 细腻克制", new("#F5F2F9", "#FCFAFF", "#ECE5F4", "#3C3249", "#30263E", "#72627F", "#795A98", "#60447E", "#D4C8E0")),
        new(ApplicationTheme.Midnight, "墨海", "深海墨蓝 · 微光青色", new("#111E28", "#1B2C38", "#243947", "#E4EDF3", "#F1F7FA", "#A7BAC7", "#95CCD3", "#B8E0E5", "#506D80", true)),
    ]);

    public static ThemeOption Get(ApplicationTheme theme) =>
        Options.FirstOrDefault(option => option.Theme == theme) ?? Options[1];

    public static bool HasExtendedPalette(ApplicationTheme theme) =>
        theme is ApplicationTheme.Rose or ApplicationTheme.Sage or ApplicationTheme.Mist
            or ApplicationTheme.Lilac or ApplicationTheme.Midnight;

    public static string WithAlpha(string hex, string alpha) => $"{hex}{alpha}";
}
