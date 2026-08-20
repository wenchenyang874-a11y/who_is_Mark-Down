namespace WhoIsMarkdown.App.ViewModels;

public sealed record FontFamilyOption(
    string DisplayName,
    string? FamilyName,
    string SearchText,
    bool IsChineseFont);
