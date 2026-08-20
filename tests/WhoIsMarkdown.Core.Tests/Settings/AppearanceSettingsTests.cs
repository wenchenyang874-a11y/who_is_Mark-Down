using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void Normalize_WhenValuesAreInvalid_RestoresSafeDefaults()
    {
        AppearanceSettings settings = new()
        {
            Theme = (ApplicationTheme)999,
            EditorFontFamily = new string('x', AppearanceSettings.MaximumFontFamilyLength + 1),
            EditorFontSize = double.NaN,
            PreviewFontFamily = "  Microsoft YaHei UI  ",
            PreviewFontSize = 200,
        };

        AppearanceSettings result = settings.Normalize();

        Assert.Equal(ApplicationTheme.System, result.Theme);
        Assert.Null(result.EditorFontFamily);
        Assert.Equal(AppearanceSettings.DefaultEditorFontSize, result.EditorFontSize);
        Assert.Equal("Microsoft YaHei UI", result.PreviewFontFamily);
        Assert.Equal(AppearanceSettings.MaximumFontSize, result.PreviewFontSize);
    }
}
