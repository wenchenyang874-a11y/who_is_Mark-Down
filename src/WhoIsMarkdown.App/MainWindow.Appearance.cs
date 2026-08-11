using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Applies a user-selected local image behind the workspace. Only the path and
/// opacity are stored; the source image is neither copied, modified, nor uploaded.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer appearanceSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(350),
    };

    private bool applyingAppearanceSettings;
    private bool appearanceSavePending;

    private void InitializeAppearanceController()
    {
        appearanceSaveTimer.Tick += AppearanceSaveTimer_Tick;
    }

    private void ApplyAppearanceSettings()
    {
        applyingAppearanceSettings = true;
        try
        {
            double transparency = (1 - applicationSettings.BackgroundOpacity) * 100;
            BackgroundTransparencySlider.Value = transparency;
            AppBackgroundImage.Opacity = applicationSettings.BackgroundOpacity;
            SetBackgroundImage(applicationSettings.BackgroundImagePath);
            UpdateBackgroundControls();
        }
        finally
        {
            applyingAppearanceSettings = false;
        }
    }

    private void BackgroundSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        BackgroundSettingsPopup.IsOpen = true;
    }

    private void SelectBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择应用背景图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!TryLoadBackgroundImage(dialog.FileName))
        {
            return;
        }

        applicationSettings = applicationSettings with
        {
            BackgroundImagePath = dialog.FileName,
        };
        UpdateBackgroundControls();
        TrySaveApplicationSettings();
        UpdateStatus("已应用本地背景图片");
    }

    private void RemoveBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        AppBackgroundImage.Source = null;
        applicationSettings = applicationSettings with { BackgroundImagePath = null };
        UpdateBackgroundControls();
        TrySaveApplicationSettings();
        UpdateStatus("已移除自定义背景，原图片未删除");
    }

    private void BackgroundTransparencySlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (applyingAppearanceSettings || BackgroundTransparencyText is null)
        {
            return;
        }

        double opacity = 1 - (eventArgs.NewValue / 100);
        AppBackgroundImage.Opacity = opacity;
        applicationSettings = applicationSettings with { BackgroundOpacity = opacity };
        BackgroundTransparencyText.Text = $"透明度 {eventArgs.NewValue:0}%";
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

    private void UpdateBackgroundControls()
    {
        bool hasBackground = AppBackgroundImage.Source is not null;
        BackgroundTransparencySlider.IsEnabled = hasBackground;
        RemoveBackgroundButton.IsEnabled = hasBackground;
        BackgroundFileNameText.Text = hasBackground
            ? System.IO.Path.GetFileName(applicationSettings.BackgroundImagePath)
            : "尚未选择图片";
        BackgroundTransparencyText.Text =
            $"透明度 {(1 - applicationSettings.BackgroundOpacity) * 100:0}%";
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
