namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Represents a validation, download, or file-system failure while saving an
/// image from the generated preview. Messages are safe to show to the user.
/// </summary>
public sealed class PreviewImageSaveException : Exception
{
    public PreviewImageSaveException(string message)
        : base(message)
    {
    }

    public PreviewImageSaveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
