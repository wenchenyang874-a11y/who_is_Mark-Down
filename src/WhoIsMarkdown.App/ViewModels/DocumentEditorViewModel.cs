using WhoIsMarkdown.App.Infrastructure;
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
    private string text = string.Empty;
    private string savedText = string.Empty;
    private string? filePath;
    private bool hasUtf8Bom;
    private DocumentLineEnding lineEnding = DocumentLineEnding.None;
    private DocumentFileStamp? stamp;
    private string untitledName = "未命名-1";

    public string Text
    {
        get => text;
        set
        {
            if (SetProperty(ref text, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string? FilePath => filePath;

    public bool HasUtf8Bom => hasUtf8Bom;

    public DocumentLineEnding LineEnding => lineEnding;

    public DocumentFileStamp? Stamp => stamp;

    public bool IsDirty => !string.Equals(text, savedText, StringComparison.Ordinal);

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
            DocumentText = includeContent ? text : null,
            SavedDocumentText = includeContent ? savedText : null,
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

        text = normalized.DocumentText;
        savedText = normalized.SavedDocumentText ?? normalized.DocumentText;
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

    public DocumentWriteRequest CreateWriteRequest(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DocumentWriteRequest(path, Text, HasUtf8Bom);
    }

    public void MarkSaved(string path, DocumentFileStamp newStamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        filePath = System.IO.Path.GetFullPath(path);
        stamp = newStamp;
        savedText = text;

        NotifyDocumentMetadataChanged();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ApplyDocument(string newText, string? newFilePath, bool emitUtf8Bom, DocumentLineEnding newLineEnding, DocumentFileStamp? newStamp)
    {
        text = newText;
        savedText = newText;
        filePath = newFilePath;
        hasUtf8Bom = emitUtf8Bom;
        lineEnding = newLineEnding;
        stamp = newStamp;

        OnPropertyChanged(nameof(Text));
        NotifyDocumentMetadataChanged();
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
