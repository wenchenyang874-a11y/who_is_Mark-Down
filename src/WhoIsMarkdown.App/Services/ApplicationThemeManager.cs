using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Applies only WIMD-owned color resources. System mode follows the Windows app
/// preference; no theme files or third-party visual assets are downloaded.
/// </summary>
public static class ApplicationThemeManager
{
    public static ApplicationTheme Apply(ApplicationTheme requestedTheme)
    {
        ApplicationTheme effectiveTheme = ResolveEffectiveTheme(requestedTheme);
        ThemePalette palette = ThemePalette.For(effectiveTheme);
        Application application = Application.Current
            ?? throw new InvalidOperationException("WPF 应用尚未初始化。");

        ApplyWpfThemeMode(application, requestedTheme);

        SetBrush(application.Resources, "AccentBrush", palette.Accent);
        SetBrush(application.Resources, "AccentHoverBrush", palette.AccentHover);
        SetBrush(application.Resources, "WindowBackgroundBrush", palette.WindowBackground);
        SetBrush(application.Resources, "ShellOverlayBrush", palette.ShellOverlay);
        SetBrush(application.Resources, "TextPrimaryBrush", palette.TextPrimary);
        SetBrush(application.Resources, "TextSecondaryBrush", palette.TextSecondary);
        SetBrush(application.Resources, "EditorForegroundBrush", palette.EditorForeground);
        SetBrush(application.Resources, "SurfaceBrush", palette.Surface);
        SetBrush(application.Resources, "SurfaceMutedBrush", palette.SurfaceMuted);
        SetBrush(application.Resources, "BorderBrush", palette.Border);
        SetBrush(application.Resources, "MenuPopupBrush", palette.MenuPopup);
        SetBrush(application.Resources, "ToolbarHoverBrush", palette.ToolbarHover);
        SetBrush(application.Resources, "ToolbarPressedBrush", palette.ToolbarPressed);
        SetBrush(application.Resources, "SelectionBrush", palette.Selection);
        return effectiveTheme;
    }

    public static ApplicationTheme ResolveEffectiveTheme(ApplicationTheme requestedTheme)
    {
        return requestedTheme == ApplicationTheme.System
            ? (IsWindowsDarkMode() ? ApplicationTheme.Dark : ApplicationTheme.Light)
            : requestedTheme;
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException)
        {
            return false;
        }
    }

    private static void ApplyWpfThemeMode(Application application, ApplicationTheme requestedTheme)
    {
        // ThemeMode is still marked as an experimental WPF API in .NET 10. Use a
        // narrow reflection boundary so WIMD can fall back to its own resource
        // palette if a future runtime removes or changes that optional property.
        PropertyInfo? property = typeof(Application).GetProperty("ThemeMode");
        Type? valueType = property?.PropertyType;
        string value = requestedTheme switch
        {
            ApplicationTheme.System => "System",
            ApplicationTheme.Dark => "Dark",
            _ => "Light",
        };

        try
        {
            object? themeMode = valueType is null
                ? null
                : Activator.CreateInstance(valueType, value);
            if (themeMode is not null)
            {
                property!.SetValue(application, themeMode);
            }
        }
        catch (Exception exception) when (exception is MissingMethodException
            or TargetInvocationException
            or ArgumentException)
        {
            // WIMD-owned brushes below remain fully functional without ThemeMode.
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private sealed record ThemePalette(
        Color Accent,
        Color AccentHover,
        Color WindowBackground,
        Color ShellOverlay,
        Color TextPrimary,
        Color TextSecondary,
        Color EditorForeground,
        Color Surface,
        Color SurfaceMuted,
        Color Border,
        Color MenuPopup,
        Color ToolbarHover,
        Color ToolbarPressed,
        Color Selection)
    {
        public static ThemePalette For(ApplicationTheme theme)
        {
            return theme switch
            {
                ApplicationTheme.Dark => Create(
                    "#898BFF", "#A6A8FF", "#11151D", "#D91A1F2A", "#EDF0F7",
                    "#A9B0C0", "#EFF2F8", "#F2262C39", "#E82C3342", "#586276",
                    "#FF252B37", "#413D4760", "#5A48536D", "#4D686BDE"),
                ApplicationTheme.Warm => Create(
                    "#9A6640", "#7D4F30", "#F4EBDD", "#B3FFF7E8", "#352B23",
                    "#766858", "#3D332B", "#F2FFF9EF", "#EDEFE2D0", "#D9C5A8",
                    "#FFFFFAF2", "#EDE8DAC4", "#E5DCC7AA", "#5ACB9D72"),
                _ => Create(
                    "#5B5CE2", "#4B4CCB", "#F4F5FA", "#B3FFFFFF", "#182033",
                    "#687086", "#1A2235", "#F2FFFFFF", "#DFF7F8FC", "#DDE2EC",
                    "#FFFFFFFF", "#E9EBF9", "#DADDF3", "#665B5CE2"),
            };
        }

        private static ThemePalette Create(params string[] colors)
        {
            Color[] parsed = colors.Select(value => (Color)ColorConverter.ConvertFromString(value)).ToArray();
            return new ThemePalette(
                parsed[0], parsed[1], parsed[2], parsed[3], parsed[4], parsed[5], parsed[6],
                parsed[7], parsed[8], parsed[9], parsed[10], parsed[11], parsed[12], parsed[13]);
        }
    }
}
