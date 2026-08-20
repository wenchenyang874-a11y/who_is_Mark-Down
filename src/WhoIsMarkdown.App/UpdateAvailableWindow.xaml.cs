using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using WhoIsMarkdown.Core.Updates;

namespace WhoIsMarkdown.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the modal window lifetime; Window_Closing always disposes its cancellation source.")]
public partial class UpdateAvailableWindow : Window
{
    private readonly UpdateRelease release;
    private readonly UpdateInstallerDownloader downloader;
    private readonly string outputDirectory;
    private readonly CancellationTokenSource cancellation = new();
    private bool downloadRunning;
    private bool allowClose;

    public UpdateAvailableWindow(
        UpdateRelease release,
        UpdateInstallerDownloader downloader,
        string outputDirectory)
    {
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        this.outputDirectory = outputDirectory;
        InitializeComponent();
        VersionText.Text = $"WIMD v{release.Version} · 发布于 {release.PublishedAtUtc.ToLocalTime():yyyy-MM-dd}";
        ReleaseNotesTextBox.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "此版本未提供更新说明。"
            : release.ReleaseNotes;
    }

    public string? InstallerPath { get; private set; }

    private async void Download_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (downloadRunning)
        {
            return;
        }

        downloadRunning = true;
        DownloadButton.IsEnabled = false;
        LaterButton.Content = "取消下载";
        DownloadProgressBar.Visibility = Visibility.Visible;
        Progress<UpdateDownloadProgress> progress = new(value =>
        {
            DownloadProgressBar.Value = value.Percentage;
            DownloadStatusText.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"正在下载并校验… {value.Percentage:0}%  ({FormatSize(value.BytesReceived)} / {FormatSize(value.TotalBytes)})");
        });

        try
        {
            InstallerPath = await downloader.DownloadAsync(
                release,
                outputDirectory,
                progress,
                cancellation.Token);
            DownloadStatusText.Text = "下载完成，安装包 SHA-256 校验通过。";
            allowClose = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            allowClose = true;
            DialogResult = false;
        }
        catch (UpdateServiceException exception)
        {
            downloadRunning = false;
            DownloadButton.IsEnabled = true;
            LaterButton.Content = "稍后再说";
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            DownloadStatusText.Text = "更新下载失败，未保留未校验的安装包。";
            MessageBox.Show(this, exception.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (downloadRunning && !allowClose)
        {
            cancellation.Cancel();
            eventArgs.Cancel = true;
            return;
        }

        cancellation.Dispose();
    }

    private static string FormatSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";
    }
}
