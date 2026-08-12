using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates the recent-file projection shown by the shell. Removing an item
/// only updates local settings and never deletes or modifies the referenced file.
/// </summary>
public partial class MainWindow
{
    private readonly IApplicationSettingsStore settingsStore = CreateSettingsStore();
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
    }

    private void RecordRecentFile(string path)
    {
        applicationSettings = applicationSettings.RecordRecentFile(path, DateTimeOffset.UtcNow);
        RefreshRecentFilesView();
        TrySaveApplicationSettings();
    }

    private async void OpenRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(
                this,
                $"文件已移动或不存在：\n{path}\n\n可以点击右侧的移除按钮将它移出最近列表。",
                "找不到文件",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (await ConfirmDiscardOrSaveAsync())
        {
            await OpenDocumentAsync(path);
        }
    }

    private void RemoveRecentFile_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string path })
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

    private void TrySaveApplicationSettings()
    {
        try
        {
            settingsStore.Save(applicationSettings);
        }
        catch (ApplicationSettingsStoreException exception)
        {
            UpdateStatus(exception.Message);
        }
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
