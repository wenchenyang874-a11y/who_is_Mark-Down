using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates the recent-file projection and its non-destructive context actions.
/// Removing an item updates only local settings; shell and clipboard actions never
/// modify, rename, move, or delete the referenced file.
/// </summary>
public partial class MainWindow
{
    private readonly IApplicationSettingsStore settingsStore = CreateSettingsStore();
    private readonly IFileExplorerService fileExplorerService = new WindowsFileExplorerService();
    private readonly IClipboardTextService clipboardTextService = new WindowsClipboardTextService();
    private ApplicationSettings applicationSettings = new();

    public ObservableCollection<RecentFileItemViewModel> RecentFiles { get; } = [];

    private void LoadApplicationSettings()
    {
        try
        {
            applicationSettings = settingsStore.Load();
        }
        catch (ApplicationSettingsStoreException exception)
        {
            applicationSettings = new ApplicationSettings();
            UpdateStatus(exception.Message);
        }

        RefreshRecentFilesView();
        SetRecentPaneExpanded(applicationSettings.IsRecentPaneExpanded, persist: false);
        ApplyAppearanceSettings();
        ApplyShortcutSettings();
    }

    private void RecordRecentFile(string path)
    {
        applicationSettings = applicationSettings.RecordRecentFile(path, DateTimeOffset.UtcNow);
        RefreshRecentFilesView();
        TrySaveApplicationSettings();
    }

    private async void OpenRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetTaggedValue(sender, out string path))
        {
            await OpenRecentFileAsync(path);
        }
    }

    private async Task OpenRecentFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            ShowRecentFileMissing(path);
            return;
        }

        if (await ConfirmDiscardOrSaveAsync())
        {
            await OpenDocumentAsync(path);
        }
    }

    private void RevealRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetTaggedValue(sender, out string path))
        {
            return;
        }

        try
        {
            fileExplorerService.RevealFile(path);
            UpdateStatus("已在文件资源管理器中定位文件");
        }
        catch (FileNotFoundException)
        {
            ShowRecentFileMissing(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or InvalidOperationException
            or Win32Exception)
        {
            UpdateStatus($"无法打开文件资源管理器：{exception.Message}");
            MessageBox.Show(
                this,
                $"无法在文件资源管理器中定位该文件：\n{path}\n\n{exception.Message}",
                "打开文件资源管理器失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void CopyRecentFilePath_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetTaggedValue(sender, out string path))
        {
            await CopyRecentValueAsync(path, "文件路径");
        }
    }

    private async void CopyRecentDirectoryPath_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetTaggedValue(sender, out string path))
        {
            await CopyRecentValueAsync(path, "所在文件夹路径");
        }
    }

    private async void CopyRecentFileName_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TryGetTaggedValue(sender, out string fileName))
        {
            await CopyRecentValueAsync(fileName, "文件名");
        }
    }

    private async Task CopyRecentValueAsync(string value, string label)
    {
        bool copied = await clipboardTextService.TrySetTextAsync(value);
        if (copied)
        {
            UpdateStatus($"已复制{label}");
            return;
        }

        UpdateStatus($"无法复制{label}：剪贴板正被其他程序占用");
        MessageBox.Show(
            this,
            "Windows 剪贴板正被其他程序占用，请稍后重试。",
            "复制失败",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RemoveRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetTaggedValue(sender, out string path))
        {
            return;
        }

        applicationSettings = applicationSettings.RemoveRecentFile(path);
        RefreshRecentFilesView();
        TrySaveApplicationSettings();
        UpdateStatus("已从最近文件中移出，原文件未删除");
    }

    private void RecentPaneToggle_Click(object sender, RoutedEventArgs eventArgs)
    {
        bool requestedState = sender is MenuItem menuItem
            ? menuItem.IsChecked
            : !applicationSettings.IsRecentPaneExpanded;
        SetRecentPaneExpanded(requestedState, persist: true);
    }

    private void CollapseRecentPane_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetRecentPaneExpanded(expanded: false, persist: true);
    }

    private void SetRecentPaneExpanded(bool expanded, bool persist)
    {
        RecentPaneColumn.Width = expanded ? new GridLength(252) : new GridLength(0);
        RecentPane.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RecentPaneMenuItem.IsChecked = expanded;
        applicationSettings = applicationSettings with { IsRecentPaneExpanded = expanded };

        if (persist)
        {
            TrySaveApplicationSettings();
        }
    }

    private void RefreshRecentFilesView()
    {
        RecentFiles.Clear();
        foreach (RecentFileEntry entry in applicationSettings.RecentFiles)
        {
            RecentFiles.Add(new RecentFileItemViewModel(entry));
        }

        RecentFilesEmptyState.Visibility = RecentFiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool TrySaveApplicationSettings()
    {
        try
        {
            settingsStore.Save(applicationSettings);
            return true;
        }
        catch (ApplicationSettingsStoreException exception)
        {
            UpdateStatus(exception.Message);
            return false;
        }
    }

    private void ShowRecentFileMissing(string path)
    {
        MessageBox.Show(
            this,
            $"文件已移动或不存在：\n{path}\n\n可右键选择“移出最近记录”，该操作不会删除原文件。",
            "找不到文件",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool TryGetTaggedValue(object sender, out string value)
    {
        value = sender is FrameworkElement { Tag: string taggedValue }
            ? taggedValue
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Brand migration: WIMD reads the old WhoIsMarkdown settings once when the new
    /// file is absent, then writes all future changes to the WIMD directory.
    /// </summary>
    private static IApplicationSettingsStore CreateSettingsStore()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string newSettingsPath = Path.Combine(localApplicationData, "WIMD", "settings.json");
        string oldSettingsPath = Path.Combine(localApplicationData, "WhoIsMarkdown", "settings.json");

        if (!File.Exists(newSettingsPath) && File.Exists(oldSettingsPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newSettingsPath)!);
                File.Copy(oldSettingsPath, newSettingsPath, overwrite: false);
            }
            catch (IOException)
            {
                // Another WIMD instance may have completed the one-time copy.
            }
            catch (UnauthorizedAccessException)
            {
                // Loading defaults is safer than preventing application startup.
            }
        }

        return new JsonApplicationSettingsStore(newSettingsPath);
    }
}
