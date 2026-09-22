using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Carries the save-as choice made on an image inside the live preview. Only the
/// picture's source URL is known there, so the desktop layer resolves and validates
/// the image again before writing anything to disk.
/// </summary>
public sealed class PreviewContextImageExportRequestedEventArgs : EventArgs
{
    public PreviewContextImageExportRequestedEventArgs(
        string source,
        bool isGeneratedDiagram,
        PreviewImageExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        IsGeneratedDiagram = isGeneratedDiagram;
        Format = format;
    }

    public PreviewImageExportFormat Format { get; }

    public bool IsGeneratedDiagram { get; }

    public string Source { get; }
}
