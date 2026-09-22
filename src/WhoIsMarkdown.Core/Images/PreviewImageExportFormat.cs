namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Save-as choices offered for a preview image. The viewer window used to expose a
/// single implicit format while the WebView2 context menu exposed another, so the
/// same picture could only be saved in one shape depending on where the user clicked.
/// Both surfaces now build their entries from this one list.
/// </summary>
public enum PreviewImageExportFormat
{
    /// <summary>
    /// The picture exactly as it appears in the preview: a vector diagram keeps its
    /// SVG source and a bitmap keeps its own encoding.
    /// </summary>
    Original,

    /// <summary>
    /// A bitmap produced by re-encoding a bitmap source or rasterizing a vector one.
    /// </summary>
    Png,

    /// <summary>
    /// A self-contained page with the image embedded, so it can be opened, shared and
    /// archived without any companion file.
    /// </summary>
    Html,
}
