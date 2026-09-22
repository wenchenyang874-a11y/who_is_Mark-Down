using System.IO;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Describes a preview image URL well enough to decide which save-as entries are worth
/// showing.
///
/// This is a display decision only. Path containment, image format and remote trust are
/// still verified when the picture is resolved for saving, so a wrong guess here can
/// never widen what WIMD is willing to write. Whether the picture is a generated
/// diagram is deliberately not inferred from the URL: the preview page reports that
/// flag for the element the user actually pointed at.
/// </summary>
internal readonly record struct PreviewImageSourceDescriptor(string Extension, bool IsSvg)
{
    private const string DataUriPrefix = "data:image/";

    public static PreviewImageSourceDescriptor Describe(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string candidate = source.Trim();
        if (candidate.StartsWith(DataUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return FromDataUri(candidate);
        }

        string extension = Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            ? Path.GetExtension(uri.AbsolutePath)
            : Path.GetExtension(candidate);
        return new PreviewImageSourceDescriptor(
            extension.ToLowerInvariant(),
            extension.Equals(".svg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps an embedded image's media type to the extension the save dialog will use.
    /// An unknown type deliberately yields no extension, which keeps the menu down to
    /// "open in the viewer" instead of offering a format that cannot be saved.
    /// </summary>
    private static PreviewImageSourceDescriptor FromDataUri(string candidate)
    {
        int separator = candidate.IndexOf(';');
        string mediaType = separator > 0
            ? candidate[DataUriPrefix.Length..separator]
            : candidate[DataUriPrefix.Length..];
        return mediaType.ToLowerInvariant() switch
        {
            "svg+xml" => new PreviewImageSourceDescriptor(".svg", true),
            "png" => new PreviewImageSourceDescriptor(".png", false),
            "jpeg" or "jpg" => new PreviewImageSourceDescriptor(".jpg", false),
            "gif" => new PreviewImageSourceDescriptor(".gif", false),
            "bmp" => new PreviewImageSourceDescriptor(".bmp", false),
            "webp" => new PreviewImageSourceDescriptor(".webp", false),
            _ => new PreviewImageSourceDescriptor(string.Empty, false),
        };
    }
}
