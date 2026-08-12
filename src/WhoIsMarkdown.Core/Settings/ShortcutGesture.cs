namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Stores a platform-neutral keyboard gesture in local application settings.
/// The WPF layer translates <see cref="Key"/> to its native key enumeration.
/// </summary>
public sealed record ShortcutGesture
{
    public required string Key { get; init; }

    public bool Control { get; init; }

    public bool Shift { get; init; }

    public bool Alt { get; init; }

    /// <summary>
    /// Canonicalizes persisted key names. WPF exposes the physical backtick key as
    /// both OemTilde and Oem3 but reports Oem3 at runtime; storing one spelling
    /// prevents a valid Ctrl+backtick assignment from silently missing dispatch.
    /// </summary>
    public ShortcutGesture Normalize()
    {
        string normalizedKey = Key?.Trim() ?? string.Empty;
        if (normalizedKey.Equals("OemTilde", StringComparison.OrdinalIgnoreCase))
        {
            normalizedKey = "Oem3";
        }

        return this with { Key = normalizedKey };
    }

    public string GetIdentity()
    {
        ShortcutGesture normalized = Normalize();
        return $"{normalized.Control}:{normalized.Shift}:{normalized.Alt}:{normalized.Key.ToUpperInvariant()}";
    }
}
