namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Describes the newline convention detected in a loaded text document.
/// The original text is never normalized automatically; this value is metadata
/// used by the editor when it needs to insert or report line endings.
/// </summary>
public enum DocumentLineEnding
{
    None,
    CrLf,
    Lf,
    Cr,
    Mixed,
}
