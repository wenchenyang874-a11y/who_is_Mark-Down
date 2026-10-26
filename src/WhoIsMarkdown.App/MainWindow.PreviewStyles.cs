using System.Windows;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Resolve styles from the active file path, never from the previous document in
/// this window. File preferences and global defaults are persisted independently;
/// source text, dirty state and browser navigation remain untouched.
/// </summary>
public partial class MainWindow
{
    private PreviewStyleOverrides previewStyleOverrides =>
        applicationSettings.PreviewStyleFiles.GetOverride(document.FilePath);

    private void PreviewStyleSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        string? targetPath = document.FilePath;
        PreviewStyleSettingsWindow dialog = new(applicationSettings.PreviewStyles, previewStyleOverrides, targetPath)
        {
            Owner = this,
        };
        // An earlier asynchronous file open can complete while this modal is up.
        // Bind the edit to the path shown by the dialog, not to a later document.
        Action<PreviewStyleSettings, PreviewStyleOverrides> apply = (global, local) => ApplyPreviewStyles(global, local, targetPath);
        dialog.StyleApplied += apply;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            dialog.StyleApplied -= apply;
        }
    }

    private void ApplyPreviewStyles(PreviewStyleSettings globalStyles, PreviewStyleOverrides fileOverrides, string? targetPath)
    {
        PreviewStyleFileSettings files = targetPath is { } path
            ? applicationSettings.PreviewStyleFiles.WithOverride(path, fileOverrides)
            : applicationSettings.PreviewStyleFiles;
        bool globalChanged = globalStyles != applicationSettings.PreviewStyles;
        bool fileChanged = files != applicationSettings.PreviewStyleFiles;
        if (globalChanged || fileChanged)
        {
            applicationSettings = applicationSettings with { PreviewStyles = globalStyles.Normalize(), PreviewStyleFiles = files };
            TrySaveApplicationSettings();
        }

        RefreshPreviewStyleSheet();
        UpdateStatus(targetPath is null ? "已应用全局预览样式；保存为文件后可设置文件覆盖" : globalChanged
            ? "已应用文件样式与全局默认，设置正在保存；其他已开窗口不实时同步"
            : "已应用所选文件的预览样式，设置保存在本机");
    }

    private void RefreshPreviewStyleSheet()
    {
        // Update the existing appearance sheet only. Re-rendering Markdown or
        // navigating the browser would needlessly disturb task/scroll state.
        previewAppearanceStyleSheet = PreviewAppearanceStyleBuilder.Build(
            effectiveTheme, applicationSettings.Appearance,
            previewStyleOverrides.Resolve(applicationSettings.PreviewStyles));
        _ = ApplyPreviewAppearanceAsync();
    }
}
