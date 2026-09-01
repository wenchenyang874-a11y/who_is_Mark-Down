namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Reports that an SVG cannot be converted to WIMD's non-interactive static profile.
/// </summary>
public sealed class SafeSvgException : Exception
{
    public SafeSvgException(string message)
        : base(message)
    {
    }

    public SafeSvgException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
