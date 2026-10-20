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
    private bool settingsChangedBeforeLoad;

    public ObservableCollection<RecentFileItemViewModel> RecentFiles { get; } = [];

    private async Task LoadApplicationSettingsAsync()
    {
        ApplicationSettings loaded;
        try
        {
            loaded = await DiagnosticLog.MeasureAsync(DiagnosticStage.SettingsLoad,
                () => Task.Run(settingsStore.Load).WaitAsync(TimeSpan.FromSeconds(15), windowLifetimeCancellation.Token), diagnosticWindowId);
        }
        catch (OperationCanceledException) when (windowClosed) { return; }
        catch (TimeoutException)
        {
            loaded = new ApplicationSettings();
            UpdateStatus("读取设置等待超过 15 秒，使用默认设置；底层读取可能仍在执行。");
        }
        catch (ApplicationSettingsStoreException exception)
        {
            loaded = new ApplicationSettings();
            UpdateStatus(exception.Message);
        }

        if (windowClosed) return;
        using DiagnosticOperation? apply = DiagnosticLog.Current?.Begin(DiagnosticStage.SettingsApply, diagnosticWindowId);
        // The shell stays interactive while loading. Preserve explicit early
        // changes relative to defaults instead of overwriting them with late I/O.
        applicationSettings = settingsChangedBeforeLoad
            ? ApplicationSettingsChanges.Merge(loaded, new ApplicationSettings(), applicationSettings) : loaded;
        settingsWriter = new ApplicationSettingsWriter(new DiagnosticSettingsStore(settingsStore, diagnosticWindowId), loaded);
        settingsWriter.WriteCompleted += SettingsWriter_WriteCompleted;
        if (settingsChangedBeforeLoad) settingsWriter.Enqueue(applicationSettings);
        DiagnosticLog.Measure(DiagnosticStage.SettingsProjection, () =>
        {
            RefreshRecentFilesView();
            SetRecentPaneExpanded(applicationSettings.IsRecentPaneExpanded, persist: false);
        }, diagnosticWindowId);
        DiagnosticLog.Measure(DiagnosticStage.ThemeAndFonts, ApplyAppearanceSettings, diagnosticWindowId);
        DiagnosticLog.Measure(DiagnosticStage.ShortcutBindings, ApplyShortcutSettings, diagnosticWindowId);
        DiagnosticLog.Measure(DiagnosticStage.SettingsLayout, () =>
        {
            RefreshFileOpenViewModeMenu();
            RefreshExternalChangeMenu();
            ApplyFileOpenViewMode();
        }, diagnosticWindowId);
        apply?.Succeed();
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
        if (windowClosed)
        {
            return;
        }

        long requestVersion = Interlocked.Increment(ref documentOpenVersion);
        UpdateStatus("正在检查最近文件…");
        bool exists;
        try
        {
            // A disconnected recent path must not keep a closed window alive or
            // reopen a stale document after a newer open/new request has won.
            exists = await DiagnosticLog.MeasureAsync(DiagnosticStage.RecentFileCheck,
                () => Task.Run(() => File.Exists(path)).WaitAsync(windowLifetimeCancellation.Token), diagnosticWindowId);
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            return;
        }

        if (windowClosed || requestVersion != Volatile.Read(ref documentOpenVersion))
        {
            return;
        }

        if (!exists)
        {
            ShowRecentFileMissing(path);
            return;
        }

        if (await ConfirmDiscardOrSaveAsync())
        {
            await OpenDocumentAsync(path);
        }
    }

    private async void RevealRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryGetTaggedValue(sender, out string path))
        {
            return;
        }

        try
        {
            UpdateStatus("正在检查文件位置…");
            await Task.Run(() => fileExplorerService.RevealFile(path));
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

    private void ExpandRecentPane_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetRecentPaneExpanded(expanded: true, persist: true);
    }

    private void SetRecentPaneExpanded(bool expanded, bool persist)
    {
        // Keep a dedicated gutter visible while the pane is collapsed. Previously
        // the only restore control lived inside the pane and disappeared with it.
        RecentPaneColumn.Width = expanded ? new GridLength(252) : new GridLength(0);
        RecentPaneGutterColumn.Width = expanded ? new GridLength(6) : new GridLength(36);
        RecentPane.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandRecentPaneButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        RecentPaneMenuItem.IsChecked = expanded;
        applicationSettings = applicationSettings with { IsRecentPaneExpanded = expanded };

        if (persist)
        {
            TrySaveApplicationSettings();
        }
    }

    private void RefreshRecentFilesView()
    {
        // Bug fix: never probe recent paths while projecting the startup UI.
        // File.Exists can block for minutes on disconnected mapped/network drives;
        // explicit recent-file actions validate their one target on a worker thread.
        RecentFiles.Clear();
        foreach (RecentFileEntry entry in applicationSettings.RecentFiles)
        {
            RecentFiles.Add(new RecentFileItemViewModel(entry, document.FilePath));
        }

        RecentFilesEmptyState.Visibility = RecentFiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>True means queued, not durably saved. Worker failures are reported separately.</summary>
    private bool TrySaveApplicationSettings()
    {
        if (windowClosed) return false;
        if (settingsWriter is null)
        {
            settingsChangedBeforeLoad = true;
            return true;
        }
        settingsWriter.Enqueue(applicationSettings);
        return true;
    }

    private void ShowRecentFileMissing(string path)
    {
        if (windowClosed)
        {
            return;
        }

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

        // No startup disk probes on the dispatcher. Legacy fallback is read on
        // the load worker and migrated by the first successful background write.
        return new JsonApplicationSettingsStore(newSettingsPath, oldSettingsPath);
    }
}
