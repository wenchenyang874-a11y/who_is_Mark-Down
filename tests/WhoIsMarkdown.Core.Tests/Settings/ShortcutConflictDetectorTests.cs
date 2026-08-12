using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ShortcutConflictDetectorTests
{
    [Fact]
    public void FindConflicts_WhenGestureCaseDiffers_ReportsBothCommands()
    {
        Dictionary<string, ShortcutGesture> assignments = new()
        {
            ["format.bold"] = new ShortcutGesture { Key = "B", Control = true },
            ["format.italic"] = new ShortcutGesture { Key = "b", Control = true },
        };

        IReadOnlyList<IReadOnlyList<string>> conflicts =
            ShortcutConflictDetector.FindConflicts(assignments);

        Assert.Equal(["format.bold", "format.italic"], Assert.Single(conflicts));
    }

    [Fact]
    public void FindConflicts_WhenBacktickUsesWpfAlias_ReportsConflict()
    {
        Dictionary<string, ShortcutGesture> assignments = new()
        {
            ["format.strike"] = new ShortcutGesture { Key = "OemTilde", Control = true },
            ["format.custom"] = new ShortcutGesture { Key = "Oem3", Control = true },
        };

        IReadOnlyList<IReadOnlyList<string>> conflicts =
            ShortcutConflictDetector.FindConflicts(assignments);

        Assert.Equal(["format.custom", "format.strike"], Assert.Single(conflicts));
    }
    [Fact]
    public void FindConflicts_WhenModifiersDiffer_ReturnsNoConflict()
    {
        Dictionary<string, ShortcutGesture> assignments = new()
        {
            ["format.bold"] = new ShortcutGesture { Key = "B", Control = true },
            ["format.italic"] = new ShortcutGesture
            {
                Key = "B",
                Control = true,
                Shift = true,
            },
        };

        Assert.Empty(ShortcutConflictDetector.FindConflicts(assignments));
    }
}
