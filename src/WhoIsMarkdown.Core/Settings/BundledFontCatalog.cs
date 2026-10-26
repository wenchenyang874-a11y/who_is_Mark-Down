namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Only audited, unmodified OFL font assets belong here. IDs are persisted in
/// settings; paths and CSS family aliases are fixed host data, never user input.
/// Files stay app-local and are loaded on demand, without system registration.
/// </summary>
public sealed record BundledFont(string Id, string DisplayName, string FamilyName,
    string RelativePath, string LicenseDirectory, bool IsChinese, string Description);

public static class BundledFontCatalog
{
    public const string VirtualHost = "wimd-fonts.invalid";
    public static IReadOnlyList<BundledFont> Fonts { get; } = Array.AsReadOnly<BundledFont>(
    [
        new("wimd:lxgw-wenkai", "霞鹜文楷", "LXGW WenKai", "lxgw-wenkai/LXGWWenKai-Regular.ttf", "lxgw-wenkai", true, "温润自然的楷书风格"),
        new("wimd:lxgw-wenkai-mono", "霞鹜文楷等宽", "LXGW WenKai Mono", "lxgw-wenkai-mono/LXGWWenKaiMono-Regular.ttf", "lxgw-wenkai-mono", true, "等宽西文与中文文楷搭配，适合中英混排"),
        new("wimd:lxgw-marker-gothic", "霞鹜漫黑", "LXGW Marker Gothic", "lxgw-marker-gothic/LXGWMarkerGothic-Regular.ttf", "lxgw-marker-gothic", true, "轻松清晰的马克笔风格中文黑体"),
        new("wimd:jetbrains-mono", "JetBrains Mono", "JetBrains Mono", "jetbrains-mono/JetBrainsMono-Regular.ttf", "jetbrains-mono", false, "清晰易辨的代码等宽字体"),
        new("wimd:fira-code", "Fira Code", "Fira Code", "fira-code/FiraCode-Regular.ttf", "fira-code", false, "简洁的代码等宽字体"),
    ]);

    public static BundledFont? Find(string? id) => Fonts.FirstOrDefault(font =>
        string.Equals(font.Id, id, StringComparison.Ordinal));

    public static string CssFamily(BundledFont font) => "WIMD " + font.FamilyName;
}
