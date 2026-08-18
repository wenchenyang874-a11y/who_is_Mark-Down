namespace WhoIsMarkdown.Core.Images;

public sealed class ImageHostUploadException : Exception
{
    public ImageHostUploadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
