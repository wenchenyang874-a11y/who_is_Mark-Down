using System.IO;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Documents;
using WhoIsMarkdown.Core.Lifecycle;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class EditorDocumentSessionTests
{
    [Fact]
    public void Input_LargeDocument_DoesNotAllocateFullTextPerChange()
    {
        EditorDocumentSession session = new(new string('a', 1024 * 1024));
        session.Document.Insert(session.Document.TextLength, "warmup");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            session.Document.Insert(session.Document.TextLength, "x");
            _ = session.IsDirty;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Generous allocation bound, not a machine-dependent timing assertion:
        // 100 edits together must cost less than one 1 MiB UTF-16 flattening.
        Assert.True(allocated < 1024 * 1024, $"Input allocated {allocated} bytes");
    }

    [Fact]
    public void Revision_IdenticalReplacement_DoesNotMarkCleanTextDirty()
    {
        EditorDocumentSession session = new("unchanged");
        session.Document.Replace(0, 9, "unchanged");
        Assert.False(session.IsDirty);
        session.Document.UndoStack.Undo();
        Assert.False(session.IsDirty);
        session.Document.UndoStack.Redo();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Revision_UndoRedo_ReturnsToSavedBaseline()
    {
        EditorDocumentSession session = new("初始\r\n");
        Assert.False(session.IsDirty);
        session.Document.Insert(session.Document.TextLength, "第一处");
        EditorTextSnapshot saved = session.Capture();
        Assert.True(session.MarkSaved(saved));
        Assert.False(session.IsDirty);
        session.Document.Insert(0, "第二处");
        Assert.True(session.IsDirty);
        session.Document.UndoStack.Undo();
        Assert.Equal(saved.ReadText(), session.Document.Text);
        Assert.False(session.IsDirty);
        session.Document.UndoStack.Undo();
        Assert.True(session.IsDirty);
        session.Document.UndoStack.Redo();
        Assert.False(session.IsDirty);
        session.Document.UndoStack.Redo();
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void Revision_GroupedEditsAndBranchedHistory_DoNotReuseSavedIdentity()
    {
        EditorDocumentSession session = new("base");
        using (session.Document.RunUpdate())
        {
            session.Document.Insert(0, "a");
            session.Document.Insert(0, "b");
        }

        Assert.True(session.IsDirty);
        session.Document.UndoStack.Undo();
        Assert.Equal("base", session.Document.Text);
        Assert.False(session.IsDirty);
        session.Document.UndoStack.Redo();
        session.MarkSaved(session.Capture());
        session.Document.UndoStack.Undo();
        session.Document.Insert(0, "different");
        Assert.True(session.IsDirty);
        Assert.False(session.Document.UndoStack.CanRedo);
    }

    [Fact]
    public void Save_EditsWhilePending_AcknowledgesOnlyCapturedRevision()
    {
        EditorDocumentSession session = new("base");
        session.Document.Insert(0, "saved-");
        EditorTextSnapshot pending = session.Capture();
        session.Document.Insert(0, "later-");
        session.MarkSaved(pending);
        Assert.True(session.IsDirty);
        Assert.Equal("saved-base", session.SavedText);
        session.Document.UndoStack.Undo();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task Snapshot_ReadOnWorkerAfterFurtherEdits_RemainsImmutable()
    {
        EditorDocumentSession session = new("中文初稿");
        EditorTextSnapshot snapshot = session.Capture();
        session.Document.Text = "new";
        Assert.Equal("new", session.Document.Text);
        Assert.Equal("中文初稿", await Task.Run(snapshot.ReadText, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Save_DifferentDocumentWithSameRevision_RejectsOldSnapshot()
    {
        DocumentEditorViewModel model = new();
        model.Text = "old";
        EditorTextSnapshot snapshot = model.CaptureSnapshot();
        model.StartNew(2);
        model.Text = "new";
        Assert.False(model.MarkSaved(snapshot, Path.GetFullPath("old.md"), new(3, DateTime.UtcNow)));
        Assert.Null(model.FilePath);
        Assert.Equal("new", model.Text);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void Recovery_DirtySavedAndUntitledDocuments_PreservesTextAndBaseline()
    {
        DocumentEditorViewModel model = new();
        string path = Path.GetFullPath("中文 空格.md");
        model.Load(new(path, "baseline", true, DocumentLineEnding.CrLf, new(8, DateTime.UtcNow)));
        model.EditorDocument.Insert(0, "pending-");
        EditorTextSnapshot saved = model.CaptureSnapshot();
        model.EditorDocument.Insert(0, "later-");
        Assert.True(model.MarkSaved(saved, path, new(16, DateTime.UtcNow)));
        UpdateRestartWindowState state = model.AddDocumentRecoveryTo(new());
        Assert.Equal("later-pending-baseline", state.DocumentText);
        Assert.Equal("pending-baseline", state.SavedDocumentText);
        DocumentEditorViewModel restored = new();
        restored.RestoreAfterUpdate(state);
        Assert.True(restored.IsDirty);
        Assert.Equal(model.Text, restored.Text);
        Assert.True(restored.HasUtf8Bom);
        Assert.Equal(DocumentLineEnding.CrLf, restored.LineEnding);
        restored.EditorDocument.Insert(0, "edit");
        restored.EditorDocument.UndoStack.Undo();
        Assert.True(restored.IsDirty);
        Assert.Equal(model.Text, restored.Text);
        restored.MarkSaved(restored.CaptureSnapshot(), path, new(22, DateTime.UtcNow));
        Assert.False(restored.IsDirty);
        Assert.Null(restored.AddDocumentRecoveryTo(new()).DocumentText);
        restored.StartNew(3);
        Assert.Equal(string.Empty, restored.AddDocumentRecoveryTo(new()).DocumentText);
        Assert.False(restored.IsDirty);
    }

    [Fact]
    public void SaveAs_WithPendingEdits_ChangesPathWithoutClearingNewEdits()
    {
        DocumentEditorViewModel model = new();
        model.Text = "save-as";
        EditorTextSnapshot pending = model.CaptureSnapshot();
        model.EditorDocument.Insert(0, "new-");
        string path = Path.GetFullPath("another.md");
        Assert.True(model.MarkSaved(pending, path, new(7, DateTime.UtcNow)));
        Assert.Equal(path, model.FilePath);
        Assert.True(model.IsDirty);
        model.EditorDocument.UndoStack.Undo();
        Assert.False(model.IsDirty);
    }
}
