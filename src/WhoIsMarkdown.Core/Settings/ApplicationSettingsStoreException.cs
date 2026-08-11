namespace WhoIsMarkdown.Core.Settings;

public sealed class ApplicationSettingsStoreException : Exception
{
    public ApplicationSettingsStoreException(string message, string path, Exception innerException)
        : base(message, innerException)
    {
        Path = path;
    }

    public string Path { get; }
}
