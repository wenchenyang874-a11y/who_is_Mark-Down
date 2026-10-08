namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Opening policy is separate from the three actual layouts: remembering a layout
/// must never become a fourth F9 state or override an installer recovery snapshot.
/// </summary>
public enum FileOpenViewMode
{
    RememberLast,
    EditorOnly,
    EditorAndPreview,
    PreviewOnly,
}
