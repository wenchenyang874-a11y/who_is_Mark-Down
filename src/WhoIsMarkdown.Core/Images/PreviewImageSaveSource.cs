using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Describes one validated preview image. Construction remains internal so callers
/// cannot bypass path containment or remote-image trust checks before saving.
/// </summary>
public sealed class PreviewImageSaveSource
{
    internal PreviewImageSaveSource(
        PreviewImageSourceKind kind,
        string value,
        string extension,
        string suggestedFileName,
        RemoteImagePolicy? remoteImagePolicy = null)
    {
        Kind = kind;
        Value = value;
        Extension = extension;
        SuggestedFileName = suggestedFileName;
        RemoteImagePolicy = remoteImagePolicy;
    }

    public string Extension { get; }

    public bool RequiresNetwork => Kind == PreviewImageSourceKind.RemoteHttps;

    public string SuggestedFileName { get; }

    internal PreviewImageSourceKind Kind { get; }

    internal RemoteImagePolicy? RemoteImagePolicy { get; }

    internal string Value { get; }
}

internal enum PreviewImageSourceKind
{
    LocalFile,
    LocalSvg,
    DataUri,
    GeneratedSvg,
    RemoteHttps,
}
