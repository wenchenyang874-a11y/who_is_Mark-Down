namespace WhoIsMarkdown.Core.Settings;

public enum ExternalFileChangeBehavior
{
    Prompt,
    AutoReload,
}

/// <summary>
/// Local path-scoped preferences only; no document contents or filesystem probes.
/// Overrides survive removal from recent files and apply to that exact path, not
/// a similarly named file in another directory. Missing/invalid values stay safe.
/// </summary>
public sealed record ExternalFileChangeSettings
{
    public ExternalFileChangeBehavior DefaultBehavior { get; init; }

    public IReadOnlyDictionary<string, ExternalFileChangeBehavior> FileOverrides { get; init; }
        = new Dictionary<string, ExternalFileChangeBehavior>(StringComparer.OrdinalIgnoreCase);

    public ExternalFileChangeSettings Normalize()
    {
        Dictionary<string, ExternalFileChangeBehavior> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, ExternalFileChangeBehavior behavior) in FileOverrides
            ?? new Dictionary<string, ExternalFileChangeBehavior>())
        {
            string? fullPath = NormalizePath(path);
            if (fullPath is not null && Enum.IsDefined(behavior)) normalized[fullPath] = behavior;
        }
        return this with
        {
            DefaultBehavior = Enum.IsDefined(DefaultBehavior) ? DefaultBehavior : ExternalFileChangeBehavior.Prompt,
            FileOverrides = normalized,
        };
    }

    public ExternalFileChangeBehavior? GetOverride(string? path)
    {
        string? fullPath = NormalizePath(path);
        return fullPath is not null && FileOverrides.TryGetValue(fullPath, out ExternalFileChangeBehavior behavior)
            ? behavior : null;
    }

    public ExternalFileChangeBehavior Resolve(string? path) => GetOverride(path) ?? DefaultBehavior;

    public ExternalFileChangeSettings WithOverride(string path, ExternalFileChangeBehavior? behavior)
    {
        string fullPath = NormalizePath(path) ?? throw new ArgumentException("文件路径无效。", nameof(path));
        ExternalFileChangeSettings normalized = Normalize();
        Dictionary<string, ExternalFileChangeBehavior> updated = new(normalized.FileOverrides, StringComparer.OrdinalIgnoreCase);
        if (behavior is { } value && Enum.IsDefined(value)) updated[fullPath] = value;
        else updated.Remove(fullPath);
        return normalized with { FileOverrides = updated };
    }

    /// <summary>
    /// Merge individual path changes, including a return to the global default.
    /// Replacing the whole dictionary would erase another window's file choices.
    /// Same-path conflicts use the last committed explicit change.
    /// </summary>
    internal static ExternalFileChangeSettings Merge(ExternalFileChangeSettings disk,
        ExternalFileChangeSettings before, ExternalFileChangeSettings after)
    {
        Dictionary<string, ExternalFileChangeBehavior> merged = new(disk.FileOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (string path in before.FileOverrides.Keys.Union(after.FileOverrides.Keys, StringComparer.OrdinalIgnoreCase))
        {
            ExternalFileChangeBehavior? oldValue = before.GetOverride(path);
            ExternalFileChangeBehavior? newValue = after.GetOverride(path);
            if (oldValue == newValue) continue;
            if (newValue is { } value) merged[path] = value;
            else merged.Remove(path);
        }
        return new()
        {
            DefaultBehavior = before.DefaultBehavior == after.DefaultBehavior ? disk.DefaultBehavior : after.DefaultBehavior,
            FileOverrides = merged,
        };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return null; }
    }
}
