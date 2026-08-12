using System.Windows;
using WhoIsMarkdown.App.ViewModels;

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

    private void SetWorkspaceViewMode(WorkspaceViewMode mode)
    {
        workspaceViewMode = mode;
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

        if (showPreview)
        {
            SchedulePreview();
        }

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
