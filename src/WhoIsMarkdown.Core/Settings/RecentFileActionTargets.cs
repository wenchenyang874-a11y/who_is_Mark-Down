namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Provides normalized, non-destructive shell action targets for a recent file.
/// Keeping both targets together prevents UI commands from deriving paths differently.
/// </summary>
public sealed record RecentFileActionTargets(string FilePath, string DirectoryPath)
{
    public static RecentFileActionTargets Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string filePath = System.IO.Path.GetFullPath(path);
        string? directoryPath = System.IO.Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("The file path does not have a containing directory.", nameof(path));
        }

        return new RecentFileActionTargets(filePath, directoryPath);
    }
}
