namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Describes an image that has already passed preview source validation and was
/// materialized into WIMD's private viewer cache.
/// </summary>
public sealed class PreparedPreviewImage
{
    internal PreparedPreviewImage(
        string filePath,
        string extension,
        string suggestedFileName,
        bool isGeneratedSvg = false)
    {
        FilePath = filePath;
        Extension = extension;
        SuggestedFileName = suggestedFileName;
        IsGeneratedSvg = isGeneratedSvg;
    }

    public string Extension { get; }

    public string FilePath { get; }

    public string SuggestedFileName { get; }

    internal bool IsGeneratedSvg { get; }
}
