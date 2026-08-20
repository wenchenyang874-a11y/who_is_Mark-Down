namespace WhoIsMarkdown.Core.Updates;

public sealed class UpdateServiceException : Exception
{
    public UpdateServiceException(string message)
        : base(message)
    {
    }

    public UpdateServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
