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
        bool isGeneratedSvg = false,
        bool isSanitizedSvg = false)
    {
        FilePath = filePath;
        Extension = extension;
        SuggestedFileName = suggestedFileName;
        IsGeneratedSvg = isGeneratedSvg;
        IsSanitizedSvg = isSanitizedSvg;
    }

    public string Extension { get; }

    public string FilePath { get; }

    public string SuggestedFileName { get; }

    internal bool IsGeneratedSvg { get; }

    internal bool IsSanitizedSvg { get; }

    internal bool IsSvg => IsGeneratedSvg || IsSanitizedSvg;
}
