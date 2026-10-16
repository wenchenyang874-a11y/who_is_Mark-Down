using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Workspace;

namespace WhoIsMarkdown.App;

/// <summary>
/// Bug fix: one-shot collection population blocked large-folder interaction and
/// detached directory reads could publish after refresh/collapse/close. Each root
/// generation cancels earlier waits; bounded UI batches recheck identity after yields.
/// Cancellation cannot forcibly interrupt an OS directory enumeration in progress.
/// </summary>
public partial class MainWindow
{
    private const int WorkspaceBatchSize = 128;
    private CancellationTokenSource? workspaceReadCancellation;
    private long workspaceReadVersion;
    private long workspaceOperationVersion;
    private readonly Dictionary<WorkspaceTreeItemViewModel, CancellationTokenSource> workspaceNodeLoads = [];

    private (long Version, CancellationToken Token) BeginWorkspaceRead()
    {
        CancelWorkspaceReads();
        workspaceReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(windowLifetimeCancellation.Token);
        return (workspaceReadVersion, workspaceReadCancellation.Token);
    }

    private void CancelWorkspaceReads()
    {
        workspaceReadVersion++;
        workspaceReadCancellation?.Cancel();
        workspaceReadCancellation?.Dispose();
        workspaceReadCancellation = null;
        KeyValuePair<WorkspaceTreeItemViewModel, CancellationTokenSource>[] loads = workspaceNodeLoads.ToArray();
        workspaceNodeLoads.Clear();
        foreach ((WorkspaceTreeItemViewModel node, CancellationTokenSource cancellation) in loads)
        {
            cancellation.Cancel();
            cancellation.Dispose();
            node.MarkUnloaded();
        }
    }

    private bool IsCurrentWorkspaceRead(long version, string root)
        => !windowClosed && version == workspaceReadVersion
            && string.Equals(root, workspaceRootPath, StringComparison.OrdinalIgnoreCase);

    private async Task PopulateWorkspaceAsync(IReadOnlyList<WorkspaceEntry> entries, long version, string root,
        CancellationToken token, WorkspaceTreeItemViewModel? parent = null)
    {
        string? selected = parent is null ? selectedWorkspaceItem?.Path : null;
        HashSet<string> expanded = parent is null
            ? new(WorkspaceItems.Where(item => item.IsExpanded).Select(item => item.Path), StringComparer.OrdinalIgnoreCase) : [];
        if (parent is null)
        {
            selectedWorkspaceItem = null;
            WorkspaceItems.Clear();
        }
        else parent.BeginChildrenUpdate();
        for (int index = 0; index < entries.Count; index++)
        {
            if (index % WorkspaceBatchSize == 0)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentWorkspaceRead(version, root)) return;
                if (index != 0) await Dispatcher.Yield(DispatcherPriority.Background);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentWorkspaceRead(version, root)) return;
            }
            if (parent is not null) parent.AddChild(entries[index]);
            else
            {
                WorkspaceTreeItemViewModel item = new(entries[index]) { IsExpanded = expanded.Contains(entries[index].Path) };
                if (string.Equals(item.Path, selected, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    selectedWorkspaceItem = item;
                }
                WorkspaceItems.Add(item);
            }
        }
        parent?.CompleteLoading();
        if (parent is null) WorkspaceEmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WorkspaceTree_Collapsed(object sender, RoutedEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not TreeViewItem { DataContext: WorkspaceTreeItemViewModel node }
            || !workspaceNodeLoads.Remove(node, out CancellationTokenSource? cancellation)) return;
        cancellation.Cancel();
        cancellation.Dispose();
        node.MarkUnloaded();
    }
}
