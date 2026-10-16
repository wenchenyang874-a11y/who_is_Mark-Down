using ICSharpCode.AvalonEdit.Document;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Tracks edits without flattening AvalonEdit's rope on every keystroke. Revision
/// operations travel with the editor's own undo groups, so undo/redo can return to
/// an asynchronously saved baseline without comparing the complete document.
/// </summary>
public sealed class EditorDocumentSession
{
    private readonly object identity = new();
    private long nextRevision;
    private long currentRevision;
    private long savedRevision;
    private ITextSource savedSnapshot;

    public EditorDocumentSession(string text, string? savedText = null)
    {
        Document = new TextDocument(text);
        bool restoredDirty = savedText is not null && !string.Equals(text, savedText, StringComparison.Ordinal);
        savedSnapshot = !restoredDirty
            ? Document.CreateSnapshot()
            : new StringTextSource(savedText!);
        // Recovery has no pre-install undo history. Its saved baseline cannot be
        // reached by undoing new edits; it remains dirty until an explicit save.
        savedRevision = restoredDirty ? -1 : 0;
        Document.Changed += Document_Changed;
        Document.TextChanged += (_, _) => TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? TextChanged;

    public TextDocument Document { get; }

    public bool IsDirty => currentRevision != savedRevision;

    public string SavedText => savedSnapshot.Text;

    public EditorTextSnapshot Capture() => new(identity, currentRevision, Document.CreateSnapshot());

    public bool MarkSaved(EditorTextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(identity, snapshot.DocumentIdentity))
        {
            return false;
        }

        // A save acknowledges the captured revision, not whatever the user typed
        // while disk I/O was pending. Keeping the immutable baseline also makes
        // installer recovery exact without retaining a second live text model.
        savedSnapshot = snapshot.Source;
        savedRevision = snapshot.Revision;
        return true;
    }

    private void Document_Changed(object? sender, DocumentChangeEventArgs eventArgs)
    {
        if (!Document.UndoStack.AcceptChanges)
        {
            return;
        }

        // Some explicit formatters replace a range with identical text. Compare
        // that changed range only, never flatten the live document for typing.
        if (eventArgs.InsertionLength == eventArgs.RemovalLength
            && string.Equals(eventArgs.InsertedText.Text, eventArgs.RemovedText.Text, StringComparison.Ordinal))
        {
            return;
        }

        long previousRevision = currentRevision;
        currentRevision = ++nextRevision;
        Document.UndoStack.PushOptional(new RevisionChange(this, previousRevision, currentRevision));
    }

    private sealed class RevisionChange(EditorDocumentSession owner, long before, long after) : IUndoableOperation
    {
        public void Undo() => owner.currentRevision = before;

        public void Redo() => owner.currentRevision = after;
    }
}

/// <summary>
/// Immutable data only: safe to materialize on a render/save worker without
/// accessing TextDocument, DispatcherObject, the view model, or any WPF control.
/// </summary>
public sealed record EditorTextSnapshot(object DocumentIdentity, long Revision, ITextSource Source)
{
    public string ReadText() => Source.Text;
}
