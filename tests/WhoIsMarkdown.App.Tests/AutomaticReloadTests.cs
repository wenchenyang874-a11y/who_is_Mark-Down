using System.IO;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Documents;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class AutomaticReloadTests
{
    private static LoadedDocument Loaded(string text = "before", string path = "文件.md", long length = 6) =>
        new(Path.GetFullPath(path), text, false, DocumentLineEnding.Lf, new(length, DateTime.UnixEpoch));

    [Fact]
    public void AutoReload_CleanUnchangedDocument_AcceptsNewDiskContent()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        Assert.True(model.TryAutoReload(Loaded("after", length: 5), model.CaptureSnapshot(), model.Stamp));
        Assert.Equal("after", model.Text);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void AutoReload_InputDuringRead_RetainsUnsavedText()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        model.EditorDocument.Insert(0, "local-");
        Assert.False(model.TryAutoReload(Loaded("external"), expected, model.Stamp));
        Assert.Equal("local-before", model.Text);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void AutoReload_SaveDuringRead_RejectsOldRevisionEvenIfClean()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        DocumentFileStamp? oldStamp = model.Stamp;
        model.EditorDocument.Insert(0, "saved-");
        model.MarkSaved(model.CaptureSnapshot(), model.FilePath!, new(12, DateTime.UnixEpoch.AddSeconds(1)));
        Assert.False(model.TryAutoReload(Loaded("external"), expected, oldStamp));
        Assert.Equal("saved-before", model.Text);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void AutoReload_ReloadSamePathOrSwitchToNewDocument_RejectsOldIdentity()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        model.Load(Loaded("new session"));
        Assert.False(model.TryAutoReload(Loaded("external"), expected, model.Stamp));
        Assert.False(model.TryAutoReload(Loaded("external", "other.md"), model.CaptureSnapshot(), model.Stamp));
        model.StartNew(2);
        Assert.False(model.TryAutoReload(Loaded("external"), expected, model.Stamp));
        Assert.Null(model.FilePath);
    }

    [Fact]
    public void AutoReload_EditThenUndoToCapturedBaseline_CanRefreshAgain()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        model.EditorDocument.Insert(0, "local-");
        model.EditorDocument.UndoStack.Undo();
        Assert.False(model.IsDirty);
        Assert.True(model.TryAutoReload(Loaded("external"), expected, model.Stamp));
    }

    [Fact]
    public void AutoReload_NewSavedRevisionWithSameStamp_StillRejectsOldSnapshot()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        model.Text = "saved content";
        model.MarkSaved(model.CaptureSnapshot(), model.FilePath!, model.Stamp!.Value);
        Assert.False(model.TryAutoReload(Loaded("external"), expected, model.Stamp));
        Assert.Equal("saved content", model.Text);
    }

    [Fact]
    public void AutoReload_SavedBaselineChangesWithoutInput_RejectsOldDiskRead()
    {
        DocumentEditorViewModel model = new();
        model.Load(Loaded());
        EditorTextSnapshot expected = model.CaptureSnapshot();
        DocumentFileStamp? baseline = model.Stamp;
        model.MarkSaved(expected, model.FilePath!, new(6, DateTime.UnixEpoch.AddSeconds(1)));
        Assert.False(model.TryAutoReload(Loaded("external"), expected, baseline));
    }
}
