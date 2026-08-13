namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Tracks the directory currently exposed to the preview virtual host. A directory
/// change requires one full page navigation because an existing WebView2 document
/// can retain failed image requests from the previous resource context.
/// </summary>
public sealed class PreviewResourceMappingState
{
    public string? DirectoryPath { get; private set; }

    public PreviewResourceMappingUpdate Update(string? documentPath)
    {
        string? nextDirectory = ResolveDirectory(documentPath);
        bool hasChanged = !string.Equals(
            DirectoryPath,
            nextDirectory,
            StringComparison.OrdinalIgnoreCase);
        DirectoryPath = nextDirectory;
        return new PreviewResourceMappingUpdate(nextDirectory, hasChanged);
    }

    private static string? ResolveDirectory(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(documentPath));
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }
}

public readonly record struct PreviewResourceMappingUpdate(
    string? DirectoryPath,
    bool HasChanged);
