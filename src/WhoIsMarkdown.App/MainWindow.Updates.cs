using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using WhoIsMarkdown.Core.Updates;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates user-authorized GitHub update checks. Startup network access is
/// disabled by default and becomes active only after the user enables it.
/// </summary>
public partial class MainWindow
{
    private readonly CancellationTokenSource updateCancellation = new();
    private HttpClient? updateHttpClient;
    private bool updateCheckRunning;

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs eventArgs)
    {
        await CheckForUpdatesAsync(showCurrentVersionResult: true);
    }

    private void CheckForUpdatesOnStartup_Click(object sender, RoutedEventArgs eventArgs)
    {
        applicationSettings = applicationSettings with
        {
            CheckForUpdatesOnStartup = CheckForUpdatesOnStartupMenuItem.IsChecked,
        };
        TrySaveApplicationSettings();
        UpdateStatus(applicationSettings.CheckForUpdatesOnStartup
            ? "已启用启动时检查更新；启动时会访问 GitHub Releases"
            : "已关闭启动时检查更新");
    }

    private Task StartAutomaticUpdateCheckAsync()
    {
        return applicationSettings.CheckForUpdatesOnStartup
            ? CheckForUpdatesAsync(showCurrentVersionResult: false)
            : Task.CompletedTask;
    }

    private async Task CheckForUpdatesAsync(bool showCurrentVersionResult)
    {
        if (updateCheckRunning || windowClosed)
        {
            return;
        }

        updateCheckRunning = true;
        CheckForUpdatesMenuItem.IsEnabled = false;
        UpdateStatus("正在连接 GitHub 检查更新…");
        try
        {
            HttpClient client = GetUpdateHttpClient();
            GitHubReleaseUpdateService updateService = new(client);
            Version currentVersion = GetCurrentProductVersion();
            using CancellationTokenSource checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                updateCancellation.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            UpdateRelease? release = await updateService.CheckAsync(
                currentVersion,
                checkTimeout.Token);
            if (release is null)
            {
                UpdateStatus($"WIMD v{currentVersion} 已是最新版本");
                if (showCurrentVersionResult)
                {
                    MessageBox.Show(
                        this,
                        $"当前 WIMD v{currentVersion} 已是最新版本。",
                        "检查更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            await ShowUpdateAsync(release, client);
        }
        catch (OperationCanceledException) when (windowClosed || updateCancellation.IsCancellationRequested)
        {
            // Application shutdown intentionally cancels network and disk work.
        }
        catch (OperationCanceledException)
        {
            const string message = "检查更新超时，请确认网络可以访问 GitHub 后重试。";
            UpdateStatus(message);
            if (showCurrentVersionResult)
            {
                MessageBox.Show(this, message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (UpdateServiceException exception)
        {
            UpdateStatus(exception.Message);
            if (showCurrentVersionResult)
            {
                MessageBox.Show(this, exception.Message, "检查更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            updateCheckRunning = false;
            if (!windowClosed)
            {
                CheckForUpdatesMenuItem.IsEnabled = true;
            }
        }
    }

    private async Task ShowUpdateAsync(UpdateRelease release, HttpClient client)
    {
        string outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WIMD",
            "Updates");
        UpdateAvailableWindow dialog = new(
            release,
            new UpdateInstallerDownloader(client),
            outputDirectory)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InstallerPath))
        {
            UpdateStatus("已取消本次更新");
            return;
        }

        if (!await ConfirmDiscardOrSaveAsync())
        {
            UpdateStatus("安装包已校验，保存当前文档后可重新检查更新并安装");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(dialog.InstallerPath)
            {
                UseShellExecute = true,
            });
            closeApproved = true;
            Close();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"安装包已经下载并校验，但无法启动：\n{exception.Message}\n\n文件位置：\n{dialog.InstallerPath}",
                "无法启动安装程序",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private HttpClient GetUpdateHttpClient()
    {
        if (updateHttpClient is not null)
        {
            return updateHttpClient;
        }

        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        updateHttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        return updateHttpClient;
    }

    private static Version GetCurrentProductVersion()
    {
        Version version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private void DisposeUpdateController()
    {
        updateCancellation.Cancel();
        updateCancellation.Dispose();
        updateHttpClient?.Dispose();
        updateHttpClient = null;
    }
}
