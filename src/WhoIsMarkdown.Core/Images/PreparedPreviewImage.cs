namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Describes an image that has already passed preview source validation and was
/// materialized into WIMD's private viewer cache.
/// </summary>
public sealed class PreparedPreviewImage
{
    internal PreparedPreviewImage(string filePath, string extension, string suggestedFileName)
    {
        FilePath = filePath;
        Extension = extension;
        SuggestedFileName = suggestedFileName;
    }

    public string Extension { get; }

    public string FilePath { get; }

    public string SuggestedFileName { get; }
}
