using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Owns workspace presentation modes without changing document or preview state.
/// F9 cycles Preview → Split → Editor while direct menu entries remain available.
/// </summary>
public partial class MainWindow
{
    private WorkspaceViewMode workspaceViewMode = WorkspaceViewMode.EditorAndPreview;

    private void PreviewOnlyMode_Click(object sender, RoutedEventArgs eventArgs) =>
        SetWorkspaceViewMode(WorkspaceViewMode.PreviewOnly);

    private void SplitMode_Click(object sender, RoutedEventArgs eventArgs) =>
        SetWorkspaceViewMode(WorkspaceViewMode.EditorAndPreview);

    private void EditorOnlyMode_Click(object sender, RoutedEventArgs eventArgs) =>
        SetWorkspaceViewMode(WorkspaceViewMode.EditorOnly);

    private void CycleViewMode_Click(object sender, RoutedEventArgs eventArgs) => CycleWorkspaceViewMode();

    private void CycleWorkspaceViewMode()
    {
        SetWorkspaceViewMode(workspaceViewMode switch
        {
            WorkspaceViewMode.PreviewOnly => WorkspaceViewMode.EditorAndPreview,
            WorkspaceViewMode.EditorAndPreview => WorkspaceViewMode.EditorOnly,
            _ => WorkspaceViewMode.PreviewOnly,
        });
    }

    private void FileOpenViewMode_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: string value }
            || !Enum.TryParse(value, out FileOpenViewMode mode)
            || !Enum.IsDefined(mode))
        {
            return;
        }

        // Changing the opening policy does not disrupt the document being read.
        // Snapshot this window's layout so RememberLast starts from what is visible.
        applicationSettings = applicationSettings with { FileOpenViewMode = mode, LastViewMode = workspaceViewMode };
        RefreshFileOpenViewModeMenu();
        if (TrySaveApplicationSettings())
        {
            UpdateStatus("已保存打开文件时的显示模式，下次打开文件时生效");
        }
    }

    private void RefreshFileOpenViewModeMenu()
    {
        foreach (MenuItem item in FileOpenViewModeMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is string value
                && Enum.TryParse(value, out FileOpenViewMode mode)
                && mode == applicationSettings.FileOpenViewMode;
        }
    }

    private void ApplyFileOpenViewMode()
    {
        SetWorkspaceViewMode(applicationSettings.ResolveFileOpenViewMode(), persist: false);
    }

    private void SetWorkspaceViewMode(WorkspaceViewMode mode, bool persist = true)
    {
        workspaceViewMode = mode;
        applicationSettings = applicationSettings with { LastViewMode = mode };
        if (persist)
        {
            TrySaveApplicationSettings();
        }

        bool showEditor = mode is not WorkspaceViewMode.PreviewOnly;
        bool showPreview = mode is not WorkspaceViewMode.EditorOnly;
        bool showSplitter = mode is WorkspaceViewMode.EditorAndPreview;

        EditorHost.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceSplitter.Visibility = showSplitter ? Visibility.Visible : Visibility.Collapsed;

        EditorColumn.Width = showEditor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitterColumn.Width = showSplitter ? new GridLength(6) : new GridLength(0);
        PreviewColumn.Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PreviewOnlyModeMenuItem.IsChecked = mode is WorkspaceViewMode.PreviewOnly;
        SplitModeMenuItem.IsChecked = mode is WorkspaceViewMode.EditorAndPreview;
        EditorOnlyModeMenuItem.IsChecked = mode is WorkspaceViewMode.EditorOnly;
        UpdatePreviewVisibility();

        // Bug fix: changing layout must not rebuild or navigate the WebView.
        // The existing preview DOM and scroll position remain valid while hidden.
        if (showEditor)
        {
            Editor.Focus();
        }

        UpdateStatus(mode switch
        {
            WorkspaceViewMode.PreviewOnly => "已切换到预览模式（F9 继续切换）",
            WorkspaceViewMode.EditorOnly => "已切换到编辑模式（F9 继续切换）",
            _ => "已切换到编辑 + 预览模式（F9 继续切换）",
        });
    }
}
