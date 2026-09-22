namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Turns image bytes into a PNG bitmap. WIMD has no SVG rasterizer of its own and the
/// core layer must not depend on WPF or on a browser control, so the desktop layer
/// provides this narrow seam instead of the export logic reaching across layers.
/// </summary>
public interface IPreviewImageRasterizer
{
    /// <summary>
    /// Renders <paramref name="imageBytes"/> to PNG bytes.
    /// </summary>
    /// <param name="imageBytes">
    /// Validated bytes. Vector content has already been given an explicit pixel size
    /// by the caller.
    /// </param>
    /// <param name="fileName">
    /// Name the renderer should serve the bytes under. Only the extension is
    /// significant; it tells the renderer how to advertise the content type.
    /// </param>
    /// <param name="pixelWidth">
    /// Requested width, or <see langword="null"/> to use the image's own intrinsic
    /// size. Vector sources always pass an explicit size because an SVG without width
    /// and height has none.
    /// </param>
    /// <param name="pixelHeight">
    /// Requested height, or <see langword="null"/> for the intrinsic height.
    /// </param>
    /// <param name="cancellationToken">Cancels a render that is still starting up.</param>
    public Task<byte[]> RasterizeToPngAsync(
        byte[] imageBytes,
        string fileName,
        int? pixelWidth,
        int? pixelHeight,
        CancellationToken cancellationToken = default);
}
