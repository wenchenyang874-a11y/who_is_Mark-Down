using System.Windows;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// A window override is deliberately not part of ApplicationSettings: saving an
/// unrelated preference must never promote a local visual choice to all windows.
/// The persisted global default is read by subsequent windows like other settings.
/// </summary>
public partial class MainWindow
{
    private PreviewStyleOverrides previewStyleOverrides = new();

    private void PreviewStyleSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        PreviewStyleSettingsWindow dialog = new(applicationSettings.PreviewStyles, previewStyleOverrides)
        {
            Owner = this,
        };
        dialog.StyleApplied += ApplyPreviewStyles;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            dialog.StyleApplied -= ApplyPreviewStyles;
        }
    }

    private void ApplyPreviewStyles(PreviewStyleSettings globalStyles, PreviewStyleOverrides windowOverrides)
    {
        previewStyleOverrides = windowOverrides;
        bool globalChanged = globalStyles != applicationSettings.PreviewStyles;
        if (globalChanged)
        {
            applicationSettings = applicationSettings with { PreviewStyles = globalStyles.Normalize() };
            TrySaveApplicationSettings();
        }

        // Update the existing appearance sheet only. Re-rendering Markdown or
        // navigating the browser would needlessly disturb task/scroll state.
        previewAppearanceStyleSheet = PreviewAppearanceStyleBuilder.Build(
            effectiveTheme, applicationSettings.Appearance,
            previewStyleOverrides.Resolve(applicationSettings.PreviewStyles));
        _ = ApplyPreviewAppearanceAsync();
        UpdateStatus(globalChanged
            ? "已应用编号样式，全局默认正在保存；其他窗口下次启动时读取"
            : "已应用当前窗口的预览样式");
    }
}
