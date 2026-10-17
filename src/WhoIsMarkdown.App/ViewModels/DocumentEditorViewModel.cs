using ICSharpCode.AvalonEdit.Document;
using WhoIsMarkdown.App.Infrastructure;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Documents;
using WhoIsMarkdown.Core.Lifecycle;

namespace WhoIsMarkdown.App.ViewModels;

/// <summary>
/// Owns the current editor document state. File dialogs and disk access remain in
/// the window/service layer, which allows this model to grow into multi-tab sessions
/// without coupling individual documents to WPF controls.
/// </summary>
public sealed class DocumentEditorViewModel : ObservableObject
{
    private EditorDocumentSession session = new(string.Empty);
    private string? filePath;
    private bool hasUtf8Bom;
    private DocumentLineEnding lineEnding = DocumentLineEnding.None;
    private DocumentFileStamp? stamp;
    private string untitledName = "未命名-1";

    public DocumentEditorViewModel()
    {
        session.TextChanged += Session_TextChanged;
    }

    public TextDocument EditorDocument => session.Document;

    public string Text
    {
        get => session.Capture().ReadText();
        set => session.Document.Text = value;
    }

    public string? FilePath => filePath;

    public bool HasUtf8Bom => hasUtf8Bom;

    public DocumentLineEnding LineEnding => lineEnding;

    public DocumentFileStamp? Stamp => stamp;

    public bool IsDirty => session.IsDirty;

    public string DisplayName => filePath is null ? untitledName : System.IO.Path.GetFileName(filePath);

    public string WindowTitle => $"{(IsDirty ? "*" : string.Empty)}{DisplayName} - WIMD";

    public void StartNew(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        untitledName = $"未命名-{number}";
        ApplyDocument(string.Empty, null, false, DocumentLineEnding.None, null);
    }

    public void Load(LoadedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ApplyDocument(document.Text, document.Path, document.HasUtf8Bom, document.LineEnding, document.Stamp);
    }

    public UpdateRestartWindowState AddDocumentRecoveryTo(UpdateRestartWindowState windowState)
    {
        ArgumentNullException.ThrowIfNull(windowState);
        bool includeContent = IsDirty || filePath is null;
        return windowState with
        {
            DocumentPath = filePath,
            DocumentText = includeContent ? Text : null,
            SavedDocumentText = includeContent ? session.SavedText : null,
            UntitledDisplayName = untitledName,
            HasUtf8Bom = hasUtf8Bom,
            LineEnding = lineEnding,
            DocumentStamp = stamp,
        };
    }

    public void RestoreAfterUpdate(UpdateRestartWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        UpdateRestartWindowState normalized = state.Normalize();
        if (normalized.DocumentText is null)
        {
            throw new InvalidOperationException("恢复状态不包含文档正文。");
        }

        ReplaceSession(normalized.DocumentText, normalized.SavedDocumentText);
        filePath = normalized.DocumentPath;
        untitledName = normalized.UntitledDisplayName;
        hasUtf8Bom = normalized.HasUtf8Bom;
        lineEnding = normalized.LineEnding;
        stamp = normalized.DocumentStamp;

        OnPropertyChanged(nameof(Text));
        NotifyDocumentMetadataChanged();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public EditorTextSnapshot CaptureSnapshot() => session.Capture();

    /// <summary>
    /// Bug fix: a clean document can become dirty while an automatic reload reads
    /// disk. Validate identity, revision and saved baseline again on the UI thread;
    /// even a save completing during that read must invalidate the old result.
    /// </summary>
    public bool TryAutoReload(LoadedDocument loaded, EditorTextSnapshot expected, DocumentFileStamp? expectedStamp)
    {
        EditorTextSnapshot current = CaptureSnapshot();
        if (IsDirty || !ReferenceEquals(current.DocumentIdentity, expected.DocumentIdentity)
            || current.Revision != expected.Revision || stamp != expectedStamp
            || !string.Equals(filePath, loaded.Path, StringComparison.OrdinalIgnoreCase)) return false;
        Load(loaded);
        return true;
    }

    public bool MarkSaved(EditorTextSnapshot snapshot, string path, DocumentFileStamp newStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = System.IO.Path.GetFullPath(path);
        if (!session.MarkSaved(snapshot))
        {
            return false;
        }

        filePath = normalizedPath;
        stamp = newStamp;

        NotifyDocumentMetadataChanged();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
        return true;
    }

    private void ApplyDocument(string newText, string? newFilePath, bool emitUtf8Bom, DocumentLineEnding newLineEnding, DocumentFileStamp? newStamp)
    {
        ReplaceSession(newText);
        filePath = newFilePath;
        hasUtf8Bom = emitUtf8Bom;
        lineEnding = newLineEnding;
        stamp = newStamp;

        OnPropertyChanged(nameof(Text));
        NotifyDocumentMetadataChanged();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ReplaceSession(string text, string? savedText = null)
    {
        session.TextChanged -= Session_TextChanged;
        session = new EditorDocumentSession(text, savedText);
        session.TextChanged += Session_TextChanged;
    }

    private void Session_TextChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void NotifyDocumentMetadataChanged()
    {
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(HasUtf8Bom));
        OnPropertyChanged(nameof(LineEnding));
        OnPropertyChanged(nameof(Stamp));
        OnPropertyChanged(nameof(DisplayName));
    }
}
