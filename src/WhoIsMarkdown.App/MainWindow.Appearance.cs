using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Bug fix: import a local wallpaper copy before persisting its path, so moving
/// the selected source cannot break future windows. Original files are never
/// modified/deleted/uploaded; only the managed path and visibility are settings.
/// </summary>
public partial class MainWindow
{
    private ApplicationTheme effectiveTheme = ApplicationTheme.Light;
    private string previewAppearanceStyleSheet = string.Empty;
    private long backgroundLoadVersion;
    private string? loadedBackgroundPath;
    private string? pendingBackgroundPath;

    private void InitializeAppearanceController()
    {
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    private void ApplyAppearanceSettings()
    {
        if (windowClosed)
        {
            return;
        }

        AppearanceSettings appearance = applicationSettings.Appearance.Normalize();
        effectiveTheme = ApplicationThemeManager.Apply(appearance.Theme);
        previewAppearanceStyleSheet = PreviewAppearanceStyleBuilder.Build(
            effectiveTheme, appearance, previewStyleOverrides.Resolve(applicationSettings.PreviewStyles));
        Editor.FontFamily = FontFamilyResolver.Resolve(appearance.EditorFontFamily, "Cascadia Mono, Consolas");
        Editor.FontSize = appearance.EditorFontSize;
        CheckForUpdatesOnStartupMenuItem.IsChecked = applicationSettings.CheckForUpdatesOnStartup;
        AppBackgroundImage.Opacity = applicationSettings.BackgroundOpacity;
        SetBackgroundImage(applicationSettings.BackgroundImagePath);
        _ = ApplyPreviewAppearanceAsync();
    }

    private void AppearanceSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        AppearanceSettingsWindow dialog = new(applicationSettings)
        {
            Owner = this,
        };
        dialog.BackgroundPage.DiagnosticWindowId = diagnosticWindowId;
        Action<ApplicationSettings> apply = selected => ApplyAppearanceFromDialog(selected, dialog.BackgroundPage.SelectedImage);
        dialog.AppearanceApplied += apply;
        try { dialog.ShowDialog(); }
        finally { dialog.AppearanceApplied -= apply; }
    }

    private void ApplyAppearanceFromDialog(ApplicationSettings selected, BitmapImage? selectedImage)
    {
        // Merge only fields owned by this dialog. Do not overwrite newer recent
        // files, workspace or other settings captured before the modal opened.
        applicationSettings = applicationSettings with
        {
            Appearance = selected.Appearance,
            BackgroundImagePath = selected.BackgroundImagePath,
            BackgroundOpacity = selected.BackgroundOpacity,
        };
        if (selectedImage is not null)
        {
            backgroundLoadVersion++;
            loadedBackgroundPath = selected.BackgroundImagePath;
            pendingBackgroundPath = null;
            AppBackgroundImage.Source = selectedImage;
        }
        ApplyAppearanceSettings();
        TrySaveApplicationSettings();
        UpdateStatus("已应用主题、字体与背景设置");
    }

    private async Task ApplyPreviewAppearanceAsync()
    {
        try
        {
            if (previewService is not null)
            {
                await previewService.ApplyAppearanceAsync(previewAppearanceStyleSheet);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException)
        {
            if (!windowClosed)
            {
                UpdateStatus($"无法更新预览外观：{exception.Message}");
            }
        }
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs eventArgs)
    {
        if (applicationSettings.Appearance.Theme != ApplicationTheme.System || windowClosed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(ApplyAppearanceSettings);
    }


    private void SetBackgroundImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            backgroundLoadVersion++;
            loadedBackgroundPath = null;
            pendingBackgroundPath = null;
            AppBackgroundImage.Source = null;
            return;
        }

        _ = LoadBackgroundImageAsync(path);
    }

    private async Task<bool> LoadBackgroundImageAsync(string path, bool forceReload = false)
    {
        if (windowClosed)
        {
            return false;
        }

        if (!forceReload && (string.Equals(path, loadedBackgroundPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, pendingBackgroundPath, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        long version = ++backgroundLoadVersion;
        pendingBackgroundPath = path;
        // Snapshot DPI/layout before entering the worker: it must never read WPF
        // controls. Keep the existing bitmap visible while its replacement loads.
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Clamp(Math.Max(ActualWidth, SystemParameters.PrimaryScreenWidth) * dpi.DpiScaleX, 1, 4096);
        int height = (int)Math.Clamp(Math.Max(ActualHeight, SystemParameters.PrimaryScreenHeight) * dpi.DpiScaleY, 1, 4096);
        try
        {
            BitmapImage image = await DiagnosticLog.MeasureAsync(DiagnosticStage.BackgroundLoad,
                () => Task.Run(() => BackgroundImageLoader.Load(path, width, height))
                    .WaitAsync(windowLifetimeCancellation.Token), diagnosticWindowId);
            if (windowClosed || version != backgroundLoadVersion)
            {
                return false;
            }

            AppBackgroundImage.Source = image;
            loadedBackgroundPath = path;
            return true;
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or FormatException
            or ArgumentException
            or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            if (!windowClosed && version == backgroundLoadVersion)
            {
                UpdateStatus($"无法加载背景图片：{exception.Message}");
            }

            return false;
        }
        finally
        {
            if (version == backgroundLoadVersion)
            {
                pendingBackgroundPath = null;
            }
        }
    }

    private void DisposeAppearanceController()
    {
        backgroundLoadVersion++;
        AppBackgroundImage.Source = null;
        loadedBackgroundPath = null;
        pendingBackgroundPath = null;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }
}
