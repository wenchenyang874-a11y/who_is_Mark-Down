using System.Collections.ObjectModel;
using WhoIsMarkdown.App.Infrastructure;
using WhoIsMarkdown.Core.Workspace;

namespace WhoIsMarkdown.App.ViewModels;

/// <summary>
/// Represents one lazily loaded workspace tree node. Directories start with a
/// placeholder child so WPF exposes an expander without recursively scanning the
/// complete workspace on the UI thread.
/// </summary>
public sealed class WorkspaceTreeItemViewModel : ObservableObject
{
    private bool isExpanded;
    private bool isSelected;
    public bool IsExpanded { get => isExpanded; set => SetProperty(ref isExpanded, value); }
    public bool IsSelected { get => isSelected; set => SetProperty(ref isSelected, value); }
    public bool IsLoading { get; private set; }
    private WorkspaceTreeItemViewModel()
    {
        Path = string.Empty;
        Name = "正在加载…";
        IsPlaceholder = true;
    }

    public WorkspaceTreeItemViewModel(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Path = entry.Path;
        Name = entry.Name;
        IsDirectory = entry.IsDirectory;
        if (IsDirectory)
        {
            Children.Add(new WorkspaceTreeItemViewModel());
        }
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public bool IsFile => !IsDirectory && !IsPlaceholder;

    public bool IsPlaceholder { get; }

    public bool IsActionable => !IsPlaceholder;

    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";

    public ObservableCollection<WorkspaceTreeItemViewModel> Children { get; } = [];

    public bool IsLoaded { get; private set; }

    public bool BeginLoading()
    {
        if (IsLoaded || IsLoading) return false;
        IsLoading = true;
        return true;
    }

    public void BeginChildrenUpdate() => Children.Clear();

    public void AddChild(WorkspaceEntry entry) => Children.Add(new WorkspaceTreeItemViewModel(entry));

    public void CompleteLoading()
    {
        IsLoaded = true;
        IsLoading = false;
    }

    public void ReplaceChildren(IEnumerable<WorkspaceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Children.Clear();
        foreach (WorkspaceEntry entry in entries)
        {
            Children.Add(new WorkspaceTreeItemViewModel(entry));
        }

        IsLoaded = true;
    }

    public void MarkUnloaded()
    {
        if (!IsDirectory)
        {
            return;
        }

        IsLoaded = false;
        IsLoading = false;
        Children.Clear();
        Children.Add(new WorkspaceTreeItemViewModel());
    }
}
