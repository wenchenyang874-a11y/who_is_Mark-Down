using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Carries the save-as format the user picked inside the independent image viewer.
/// The viewer already holds the materialized image, so only the choice has to travel.
/// </summary>
public sealed class PreviewImageExportRequestedEventArgs : EventArgs
{
    public PreviewImageExportRequestedEventArgs(
        PreparedPreviewImage image,
        PreviewImageExportFormat format)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Format = format;
    }

    public PreviewImageExportFormat Format { get; }

    public PreparedPreviewImage Image { get; }
}
