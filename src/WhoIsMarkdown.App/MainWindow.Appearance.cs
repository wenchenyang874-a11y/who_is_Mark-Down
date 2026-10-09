using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Applies a user-selected local image behind the complete editor and preview
/// workspace. Only the path and opacity are stored; the source image is neither
/// copied, modified, nor uploaded.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer appearanceSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(350),
    };

    private bool appearanceSavePending;
    private ApplicationTheme effectiveTheme = ApplicationTheme.Light;
    private string previewAppearanceStyleSheet = string.Empty;
    private long backgroundLoadVersion;
    private string? loadedBackgroundPath;
    private string? pendingBackgroundPath;

    private void InitializeAppearanceController()
    {
        appearanceSaveTimer.Tick += AppearanceSaveTimer_Tick;
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
        previewAppearanceStyleSheet = PreviewAppearanceStyleBuilder.Build(effectiveTheme, appearance);
        Editor.FontFamily = new FontFamily(
            appearance.EditorFontFamily ?? "Cascadia Mono, Consolas");
        Editor.FontSize = appearance.EditorFontSize;
        CheckForUpdatesOnStartupMenuItem.IsChecked = applicationSettings.CheckForUpdatesOnStartup;
        AppBackgroundImage.Opacity = applicationSettings.BackgroundOpacity;
        SetBackgroundImage(applicationSettings.BackgroundImagePath);
        _ = ApplyPreviewAppearanceAsync();
    }

    private void AppearanceSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        AppearanceSettingsWindow dialog = new(applicationSettings.Appearance)
        {
            Owner = this,
        };
        dialog.AppearanceApplied += ApplyAppearanceFromDialog;
        bool? result = dialog.ShowDialog();
        dialog.AppearanceApplied -= ApplyAppearanceFromDialog;
        if (result != true)
        {
            return;
        }

        ApplyAppearanceFromDialog(dialog.ResultSettings);
    }

    private void ApplyAppearanceFromDialog(AppearanceSettings appearance)
    {
        applicationSettings = applicationSettings with
        {
            Appearance = appearance,
        };
        ApplyAppearanceSettings();
        TrySaveApplicationSettings();
        UpdateStatus("已应用外观与字体设置");
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

    private void BackgroundSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        BackgroundSettingsWindow dialog = new(
            applicationSettings.BackgroundImagePath,
            BackgroundAppearanceScale.ToPercentage(applicationSettings.BackgroundOpacity))
        {
            Owner = this,
        };

        dialog.BackgroundImageSelected += path => ApplySelectedBackground(dialog, path);
        dialog.BackgroundRemoved += () => RemoveBackground(dialog);
        dialog.BackgroundVisibilityChanged += ApplyBackgroundVisibility;
        dialog.ShowDialog();
    }

    private async void ApplySelectedBackground(BackgroundSettingsWindow dialog, string path)
    {
        if (!await LoadBackgroundImageAsync(path, forceReload: true))
        {
            return;
        }

        applicationSettings = applicationSettings with
        {
            BackgroundImagePath = path,
        };
        if (dialog.IsVisible)
        {
            dialog.SetBackgroundPath(path);
        }
        TrySaveApplicationSettings();
        UpdateStatus("已应用本地背景图片");
    }

    private void RemoveBackground(BackgroundSettingsWindow dialog)
    {
        backgroundLoadVersion++;
        loadedBackgroundPath = null;
        pendingBackgroundPath = null;
        AppBackgroundImage.Source = null;
        applicationSettings = applicationSettings with { BackgroundImagePath = null };
        dialog.SetBackgroundPath(null);
        TrySaveApplicationSettings();
        UpdateStatus("已移除自定义背景，原图片未删除");
    }

    private void ApplyBackgroundVisibility(double percentage)
    {
        // Bug fix: the slider is a visibility control. Its previous inverse
        // mapping made 100% hide the image and 0% show it.
        double opacity = BackgroundAppearanceScale.FromPercentage(percentage);
        AppBackgroundImage.Opacity = opacity;
        applicationSettings = applicationSettings with { BackgroundOpacity = opacity };
        ScheduleAppearanceSave();
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
            BitmapImage image = await Task.Run(() => BackgroundImageLoader.Load(path, width, height))
                .WaitAsync(windowLifetimeCancellation.Token);
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

    private void ScheduleAppearanceSave()
    {
        appearanceSavePending = true;
        appearanceSaveTimer.Stop();
        appearanceSaveTimer.Start();
    }

    private void AppearanceSaveTimer_Tick(object? sender, EventArgs eventArgs)
    {
        FlushAppearanceSettings();
    }

    private void FlushAppearanceSettings()
    {
        appearanceSaveTimer.Stop();
        if (!appearanceSavePending)
        {
            return;
        }

        appearanceSavePending = false;
        TrySaveApplicationSettings();
    }

    private void DisposeAppearanceController()
    {
        backgroundLoadVersion++;
        AppBackgroundImage.Source = null;
        loadedBackgroundPath = null;
        pendingBackgroundPath = null;
        FlushAppearanceSettings();
        appearanceSaveTimer.Tick -= AppearanceSaveTimer_Tick;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
    }
}
