using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ApplicationSettingsBoundaryTests
{
    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(0.45, 0.45)]
    [InlineData(2.0, 1)]
    public void Normalize_WhenBackgroundOpacityIsOutsideRange_ClampsToValidValue(
        double opacity,
        double expected)
    {
        ApplicationSettings settings = new() { BackgroundOpacity = opacity };

        ApplicationSettings result = settings.Normalize();

        Assert.Equal(expected, result.BackgroundOpacity);
    }

    [Fact]
    public void RecordRecentFile_WhenMoreThanMaximumAreOpened_KeepsNewestEntriesOnly()
    {
        ApplicationSettings settings = new();
        DateTimeOffset start = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

        for (int index = 0; index < ApplicationSettings.MaximumRecentFiles + 3; index++)
        {
            settings = settings.RecordRecentFile(
                System.IO.Path.GetFullPath($"document-{index}.md"),
                start.AddMinutes(index));
        }

        Assert.Equal(ApplicationSettings.MaximumRecentFiles, settings.RecentFiles.Count);
        Assert.EndsWith("document-12.md", settings.RecentFiles[0].Path, StringComparison.Ordinal);
        Assert.DoesNotContain(
            settings.RecentFiles,
            entry => entry.Path.EndsWith("document-0.md", StringComparison.Ordinal));
    }
}
