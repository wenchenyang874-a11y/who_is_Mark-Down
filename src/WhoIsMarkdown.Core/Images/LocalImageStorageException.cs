namespace WhoIsMarkdown.Core.Images;

public sealed class LocalImageStorageException : Exception
{
    public LocalImageStorageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
