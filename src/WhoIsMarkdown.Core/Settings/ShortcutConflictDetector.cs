namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Detects duplicate gestures without depending on a UI framework. Empty and
/// malformed entries are ignored here and rejected by the desktop key parser.
/// </summary>
public static class ShortcutConflictDetector
{
    public static IReadOnlyList<IReadOnlyList<string>> FindConflicts(
        IEnumerable<KeyValuePair<string, ShortcutGesture>> assignments)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        return assignments
            .Where(item => item.Value is not null && !string.IsNullOrWhiteSpace(item.Value.Key))
            .GroupBy(item => item.Value.GetIdentity(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<string>)group
                .Select(item => item.Key)
                .Order(StringComparer.Ordinal)
                .ToArray())
            .ToArray();
    }
}
