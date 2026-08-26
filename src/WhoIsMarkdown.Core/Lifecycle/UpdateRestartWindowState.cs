using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.Core.Lifecycle;

/// <summary>
/// Contains only local paths and presentation state required to reopen a WIMD
/// window after an installer-controlled update. Document contents are never stored.
/// </summary>
public sealed record UpdateRestartWindowState
{
    private static readonly HashSet<string> SupportedViewModes = new(
        ["PreviewOnly", "EditorAndPreview", "EditorOnly"],
        StringComparer.Ordinal);

    public string? WorkspacePath { get; init; }

    public string? DocumentPath { get; init; }

    /// <summary>
    /// Carries update-only recovery content for dirty or untitled documents.
    /// Clean files are reopened from disk and therefore leave this value null.
    /// </summary>
    public string? DocumentText { get; init; }

    public string? SavedDocumentText { get; init; }

    public string UntitledDisplayName { get; init; } = "未命名-1";

    public bool HasUtf8Bom { get; init; }

    public DocumentLineEnding LineEnding { get; init; }

    public DocumentFileStamp? DocumentStamp { get; init; }

    public string ViewMode { get; init; } = "EditorAndPreview";

    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; } = 1320;

    public double Height { get; init; } = 820;

    public bool IsMaximized { get; init; }

    public int CaretOffset { get; init; }

    public double EditorVerticalOffset { get; init; }

    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public UpdateRestartWindowState Normalize()
    {
        return this with
        {
            WorkspacePath = TryNormalizePath(WorkspacePath),
            DocumentPath = TryNormalizePath(DocumentPath),
            SavedDocumentText = DocumentText is null
                ? null
                : SavedDocumentText ?? DocumentText,
            UntitledDisplayName = NormalizeUntitledDisplayName(UntitledDisplayName),
            LineEnding = Enum.IsDefined(LineEnding) ? LineEnding : DocumentLineEnding.None,
            DocumentStamp = NormalizeStamp(DocumentStamp),
            ViewMode = ViewMode is not null && SupportedViewModes.Contains(ViewMode)
                ? ViewMode
                : "EditorAndPreview",
            Left = NormalizeCoordinate(Left),
            Top = NormalizeCoordinate(Top),
            Width = NormalizeSize(Width, 900, 1320),
            Height = NormalizeSize(Height, 600, 820),
            CaretOffset = Math.Max(0, CaretOffset),
            EditorVerticalOffset = double.IsFinite(EditorVerticalOffset)
                ? Math.Clamp(EditorVerticalOffset, 0, 10_000_000)
                : 0,
            SavedAtUtc = SavedAtUtc == default
                ? DateTimeOffset.UtcNow
                : SavedAtUtc.ToUniversalTime(),
        };
    }

    private static string NormalizeUntitledDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未命名-1";
        }

        string normalized = new(value
            .Where(character => !char.IsControl(character))
            .Take(128)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "未命名-1" : normalized;
    }

    private static DocumentFileStamp? NormalizeStamp(DocumentFileStamp? stamp)
    {
        if (stamp is not { Length: >= 0 } value)
        {
            return null;
        }

        DateTime lastWriteTimeUtc = value.LastWriteTimeUtc.Kind == DateTimeKind.Utc
            ? value.LastWriteTimeUtc
            : value.LastWriteTimeUtc.ToUniversalTime();
        return new DocumentFileStamp(value.Length, lastWriteTimeUtc);
    }

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    private static double NormalizeCoordinate(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, -100_000, 100_000) : 0;
    }

    private static double NormalizeSize(double value, double minimum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, 20_000) : fallback;
    }
}
