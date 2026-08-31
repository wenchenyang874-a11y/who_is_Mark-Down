namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Carries an image source selected in the generated preview. The desktop layer
/// must validate and materialize it before opening the independent viewer.
/// </summary>
public sealed class PreviewImageOpenRequestedEventArgs : EventArgs
{
    public PreviewImageOpenRequestedEventArgs(
        string source,
        string? alternativeText,
        bool isGeneratedDiagram = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        AlternativeText = alternativeText;
        IsGeneratedDiagram = isGeneratedDiagram;
    }

    public string? AlternativeText { get; }

    public bool IsGeneratedDiagram { get; }

    public string Source { get; }
}
