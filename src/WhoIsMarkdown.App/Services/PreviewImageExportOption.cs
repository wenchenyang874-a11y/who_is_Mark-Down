using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// One save-as entry. The label is resolved from the core export rules when the entry
/// is built, so the viewer toolbar, the viewer's right-click menu and the live
/// preview's right-click menu all read the same text for the same choice.
/// </summary>
public sealed record PreviewImageExportOption(PreviewImageExportFormat Format, string Label);
