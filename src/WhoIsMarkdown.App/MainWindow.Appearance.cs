using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    private void InitializeAppearanceController()
    {
        appearanceSaveTimer.Tick += AppearanceSaveTimer_Tick;
    }

    private void ApplyAppearanceSettings()
    {
        AppBackgroundImage.Opacity = applicationSettings.BackgroundOpacity;
        SetBackgroundImage(applicationSettings.BackgroundImagePath);
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

    private void ApplySelectedBackground(BackgroundSettingsWindow dialog, string path)
    {
        if (!TryLoadBackgroundImage(path))
        {
            return;
        }

        applicationSettings = applicationSettings with
        {
            BackgroundImagePath = path,
        };
        dialog.SetBackgroundPath(path);
        TrySaveApplicationSettings();
        UpdateStatus("已应用本地背景图片");
    }

    private void RemoveBackground(BackgroundSettingsWindow dialog)
    {
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
            AppBackgroundImage.Source = null;
            return;
        }

        if (!TryLoadBackgroundImage(path))
        {
            applicationSettings = applicationSettings with { BackgroundImagePath = null };
        }
    }

    private bool TryLoadBackgroundImage(string path)
    {
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            AppBackgroundImage.Source = image;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or FormatException)
        {
            AppBackgroundImage.Source = null;
            UpdateStatus($"无法加载背景图片：{exception.Message}");
            return false;
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
        FlushAppearanceSettings();
        appearanceSaveTimer.Tick -= AppearanceSaveTimer_Tick;
    }
}
