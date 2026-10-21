namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Carries an image source selected in the generated preview. The desktop layer
/// must validate/materialize it before displaying pixels or enabling save. The
/// independent loading window may be shown immediately, before that work ends.
/// </summary>
public sealed class PreviewImageOpenRequestedEventArgs : EventArgs
{
    public PreviewImageOpenRequestedEventArgs(
        string source,
        string? alternativeText,
        bool isGeneratedDiagram = false,
        PreviewImageOpenOrigin origin = PreviewImageOpenOrigin.Click)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        AlternativeText = alternativeText;
        IsGeneratedDiagram = isGeneratedDiagram;
        Origin = origin;
    }

    public string? AlternativeText { get; }

    public bool IsGeneratedDiagram { get; }

    public string Source { get; }

    public PreviewImageOpenOrigin Origin { get; }
}

public enum PreviewImageOpenOrigin { Click, ContextMenu }
