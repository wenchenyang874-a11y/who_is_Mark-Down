using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Controls;

/// <summary>
/// Draft background editor embedded in Appearance. A real owner Window stays
/// alive behind the native picker; no Popup/reopen sequence can lose the panel.
/// Only Apply/Save publishes the draft. Cancellation never deletes the source.
/// </summary>
public partial class BackgroundSettingsControl : UserControl, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private long selectionVersion;
    private bool disposed;
    internal BackgroundImageStore Store { get; set; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WIMD", "Backgrounds"));
    internal int DiagnosticWindowId { get; set; }
    public string? SelectedPath { get; private set; }
    public BitmapImage? SelectedImage { get; private set; }
    public bool IsImporting { get; private set; }
    public double SelectedOpacity => BackgroundAppearanceScale.FromPercentage(BackgroundVisibilitySlider.Value);
    public event Action<bool>? ImportStateChanged;

    public BackgroundSettingsControl() { InitializeComponent(); }

    public void SetSelection(string? path, double opacity)
    {
        selectionVersion++;
        SelectedPath = path;
        SelectedImage = null;
        BackgroundThumbnail.Source = null;
        BackgroundVisibilitySlider.Value = BackgroundAppearanceScale.ToPercentage(opacity);
        BackgroundVisibilityText.Text = $"背景可见度 {BackgroundVisibilitySlider.Value:0}%";
        UpdateControls();
        if (!string.IsNullOrWhiteSpace(path)) _ = LoadThumbnailAsync(path, selectionVersion);
    }

    private async Task LoadThumbnailAsync(string path, long version)
    {
        try
        {
            BitmapImage image = await Task.Run(() => BackgroundImageLoader.Load(path, 720, 400))
                .WaitAsync(lifetime.Token);
            if (disposed || version != selectionVersion) return;
            BackgroundThumbnail.Source = image;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception exception) when (IsImageError(exception))
        {
            if (!disposed && version == selectionVersion) StatusText.Text = "原背景暂时无法预览，可重新选择。";
        }
    }

    private void SelectBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        Window? owner = Window.GetWindow(this);
        if (owner is null || IsImporting || disposed) return;
        OpenFileDialog picker = new()
        {
            Title = "选择应用背景图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(owner) == true) _ = ImportAsync(picker.FileName);
    }

    internal async Task ImportAsync(string path)
    {
        if (disposed || IsImporting) return;
        long version = ++selectionVersion;
        IsImporting = true;
        UpdateControls();
        ImportStateChanged?.Invoke(true);
        StatusText.Text = "正在复制并验证背景…";
        BackgroundImageStore store = Store;
        CancellationToken token = lifetime.Token;
        try
        {
            // Capture only immutable input for the worker. Decode at a bounded
            // display size; neither the source nor other windows' copies are removed.
            StoredBackgroundImage imported = await DiagnosticLog.MeasureAsync(DiagnosticStage.BackgroundImport,
                () => Task.Run(() => store.ImportAsync(path, 4096, 4096, token), token).WaitAsync(token), DiagnosticWindowId);
            if (disposed || version != selectionVersion) return;
            SelectedPath = imported.Path;
            SelectedImage = imported.Image;
            BackgroundThumbnail.Source = imported.Image;
            StatusText.Text = "已生成本地副本。点击“应用”或“保存”使用。";
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception exception) when (IsImageError(exception))
        {
            if (!disposed && version == selectionVersion)
                StatusText.Text = $"无法导入图片，已保留原背景：{exception.Message}";
        }
        finally
        {
            if (!disposed)
            {
                IsImporting = false;
                UpdateControls();
                ImportStateChanged?.Invoke(false);
            }
        }
    }

    private static bool IsImageError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or NotSupportedException or FormatException or ArgumentException or InvalidOperationException
        or System.Runtime.InteropServices.COMException or System.Security.SecurityException;

    private void RemoveBackground_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetSelection(null, SelectedOpacity);
        StatusText.Text = "点击“应用”或“保存”移除背景，不会删除原图片。";
    }

    private void VisibilityChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (BackgroundVisibilityText is null) return;
        BackgroundVisibilityText.Text = $"背景可见度 {eventArgs.NewValue:0}%";
        BackgroundThumbnail.Opacity = eventArgs.NewValue / 100;
    }

    private void UpdateControls()
    {
        bool hasImage = !string.IsNullOrWhiteSpace(SelectedPath);
        BackgroundFileNameText.Text = hasImage ? Path.GetFileName(SelectedPath) : "尚未选择图片";
        BackgroundFileNameText.ToolTip = SelectedPath;
        EmptyBackgroundText.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        SelectBackgroundButton.IsEnabled = !IsImporting;
        SelectBackgroundButton.Content = IsImporting ? "正在导入…" : "选择图片";
        RemoveBackgroundButton.IsEnabled = hasImage && !IsImporting;
        BackgroundVisibilitySlider.IsEnabled = hasImage;
    }

    private void ThumbnailSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        // Border.ClipToBounds alone is rectangular and does not clip child pixels
        // to CornerRadius, which used to produce square image corners.
        ((Border)sender).Clip = new RectangleGeometry(new Rect(eventArgs.NewSize), 12, 12);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        selectionVersion++;
        lifetime.Cancel();
        lifetime.Dispose();
        SelectedImage = null;
        BackgroundThumbnail.Source = null;
        ImportStateChanged = null;
        GC.SuppressFinalize(this);
    }
}
