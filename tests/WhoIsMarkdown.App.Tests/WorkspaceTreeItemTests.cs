using WhoIsMarkdown.App.ViewModels;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class WorkspaceTreeItemTests
{
    [Fact]
    public void Loading_RepeatedExpand_AdmitsOneReadAndSupportsCancelRetry()
    {
        WorkspaceTreeItemViewModel node = new(new("root", "root", true));
        Assert.True(node.BeginLoading());
        Assert.False(node.BeginLoading());
        node.MarkUnloaded();
        Assert.Single(node.Children);
        Assert.True(node.Children[0].IsPlaceholder);
        Assert.True(node.BeginLoading());
        node.BeginChildrenUpdate();
        node.AddChild(new("root/one.md", "one.md", false));
        node.CompleteLoading();
        Assert.True(node.IsLoaded);
        Assert.False(node.IsLoading);
        Assert.False(node.BeginLoading());
        Assert.True(node.Children[0].IsFile);
    }

    [Fact]
    public void Recycling_StateChanges_NotifyAndBelongToNodeNotContainer()
    {
        WorkspaceTreeItemViewModel node = new(new("root", "root", true));
        List<string?> properties = [];
        node.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        node.IsExpanded = true;
        node.IsSelected = true;
        node.IsSelected = true;
        Assert.Equal(["IsExpanded", "IsSelected"], properties);
        Assert.True(node.IsExpanded);
        Assert.True(node.IsSelected);
    }
}
