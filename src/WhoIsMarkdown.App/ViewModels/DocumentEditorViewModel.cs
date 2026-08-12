using WhoIsMarkdown.App.Infrastructure;
using WhoIsMarkdown.Core.Documents;

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
