namespace WhoIsMarkdown.App.Services;

public sealed class SecretProtectionException : Exception
{
    public SecretProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
