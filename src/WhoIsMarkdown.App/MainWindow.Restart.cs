using System.Windows;
using System.Windows.Threading;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Lifecycle;

namespace WhoIsMarkdown.App;

/// <summary>
/// Captures and restores one-time window state around an installer-controlled
/// update. Dirty text is kept only in the local recovery session and remains
/// dirty after restoration; WIMD never overwrites the source document silently.
/// </summary>
public partial class MainWindow
{
    private readonly string updateRestartWindowId = Guid.NewGuid().ToString("N");
    private readonly UpdateRestartWindowState? restoredWindowState;
    private readonly UpdateRestartSessionStore updateRestartSessionStore;
    private bool updateRestartStateSaved;

    private void ApplyRestoredWindowPlacement()
    {
        if (restoredWindowState is null)
        {
            return;
        }

        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        double virtualHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(restoredWindowState.Width, MinWidth, virtualWidth);
        Height = Math.Clamp(restoredWindowState.Height, MinHeight, virtualHeight);

        // Keep at least a title-bar-sized portion visible when monitor topology
        // changed during the update (for example, a disconnected second screen).
        const double minimumVisibleWidth = 160;
        const double minimumVisibleHeight = 48;
        Left = Math.Clamp(
            restoredWindowState.Left,
            virtualLeft - Width + minimumVisibleWidth,
            virtualLeft + virtualWidth - minimumVisibleWidth);
        Top = Math.Clamp(
            restoredWindowState.Top,
            virtualTop,
            virtualTop + virtualHeight - minimumVisibleHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void RestoreWindowInteractionState()
    {
        if (restoredWindowState is null)
        {
            return;
        }

        if (Enum.TryParse(restoredWindowState.ViewMode, out WorkspaceViewMode viewMode))
        {
            SetWorkspaceViewMode(viewMode);
        }

        int caretOffset = Math.Min(restoredWindowState.CaretOffset, Editor.Document.TextLength);
        Editor.CaretOffset = Math.Max(0, caretOffset);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (!windowClosed)
                {
                    Editor.ScrollToVerticalOffset(restoredWindowState.EditorVerticalOffset);
                    if (restoredWindowState.IsMaximized)
                    {
                        WindowState = WindowState.Maximized;
                    }
                }
            }));
    }

    private bool TryPersistUpdateRestartWindowState()
    {
        if (updateRestartStateSaved)
        {
            return true;
        }

        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty)
        {
            bounds = new Rect(Left, Top, Width, Height);
        }

        UpdateRestartWindowState state = document.AddDocumentRecoveryTo(new UpdateRestartWindowState
        {
            WorkspacePath = workspaceRootPath,
            ViewMode = workspaceViewMode.ToString(),
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            CaretOffset = Editor.CaretOffset,
            EditorVerticalOffset = Editor.VerticalOffset,
            SavedAtUtc = DateTimeOffset.UtcNow,
        });

        updateRestartStateSaved = updateRestartSessionStore.TrySaveRequestedWindow(
            updateRestartWindowId,
            state);
        return updateRestartStateSaved;
    }
}
