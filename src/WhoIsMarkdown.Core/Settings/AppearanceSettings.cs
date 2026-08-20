namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Selects a built-in WIMD color palette. Fonts are intentionally referenced by
/// installed family name only; WIMD never bundles or redistributes font files.
/// </summary>
public enum ApplicationTheme
{
    System,
    Light,
    Dark,
    Warm,
}

public sealed record AppearanceSettings
{
    public const double DefaultEditorFontSize = 15;
    public const double DefaultPreviewFontSize = 16;
    public const double MinimumFontSize = 10;
    public const double MaximumFontSize = 32;
    public const int MaximumFontFamilyLength = 128;

    public ApplicationTheme Theme { get; init; } = ApplicationTheme.System;

    public string? EditorFontFamily { get; init; }

    public double EditorFontSize { get; init; } = DefaultEditorFontSize;

    public string? PreviewFontFamily { get; init; }

    public double PreviewFontSize { get; init; } = DefaultPreviewFontSize;

    public AppearanceSettings Normalize()
    {
        ApplicationTheme normalizedTheme = Enum.IsDefined(Theme)
            ? Theme
            : ApplicationTheme.System;

        return this with
        {
            Theme = normalizedTheme,
            EditorFontFamily = NormalizeFontFamily(EditorFontFamily),
            EditorFontSize = NormalizeFontSize(EditorFontSize, DefaultEditorFontSize),
            PreviewFontFamily = NormalizeFontFamily(PreviewFontFamily),
            PreviewFontSize = NormalizeFontSize(PreviewFontSize, DefaultPreviewFontSize),
        };
    }

    private static string? NormalizeFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        return normalized.Length <= MaximumFontFamilyLength
            && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static double NormalizeFontSize(double value, double defaultValue)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumFontSize, MaximumFontSize)
            : defaultValue;
    }
}
