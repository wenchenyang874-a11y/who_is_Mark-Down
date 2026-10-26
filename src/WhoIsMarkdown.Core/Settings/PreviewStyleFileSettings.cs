namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Local, exact-path style preferences. No filesystem probe or Markdown metadata
/// is needed. Empty overrides are removed so the file follows global defaults.
/// Merge only changed paths/features, never a stale window's entire dictionary.
/// </summary>
public sealed record PreviewStyleFileSettings
{
    public IReadOnlyDictionary<string, PreviewStyleOverrides> FileOverrides { get; init; }
        = new Dictionary<string, PreviewStyleOverrides>(StringComparer.OrdinalIgnoreCase);

    public PreviewStyleFileSettings Normalize()
    {
        Dictionary<string, PreviewStyleOverrides> files = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, PreviewStyleOverrides? value) in FileOverrides
            ?? new Dictionary<string, PreviewStyleOverrides>())
        {
            string? key = NormalizePath(path);
            PreviewStyleOverrides style = (value ?? new()).Normalize();
            if (key is not null && !style.IsEmpty) files[key] = style;
        }
        return this with { FileOverrides = files };
    }

    public PreviewStyleOverrides GetOverride(string? path)
    {
        string? key = NormalizePath(path);
        return key is not null && FileOverrides is not null && FileOverrides.TryGetValue(key, out PreviewStyleOverrides? value)
            ? (value ?? new()).Normalize() : new();
    }

    public PreviewStyleFileSettings WithOverride(string path, PreviewStyleOverrides value)
    {
        string key = NormalizePath(path) ?? throw new ArgumentException("请先保存文件，再设置文件样式。", nameof(path));
        ArgumentNullException.ThrowIfNull(value);
        PreviewStyleOverrides normalized = value.Normalize();
        if (GetOverride(key) == normalized) return this;
        Dictionary<string, PreviewStyleOverrides> files = new(FileOverrides ?? new Dictionary<string, PreviewStyleOverrides>(), StringComparer.OrdinalIgnoreCase);
        if (normalized.IsEmpty) files.Remove(key);
        else files[key] = normalized;
        return this with { FileOverrides = files };
    }

    internal static PreviewStyleFileSettings Merge(PreviewStyleFileSettings disk,
        PreviewStyleFileSettings before, PreviewStyleFileSettings after)
    {
        Dictionary<string, PreviewStyleOverrides> files = new(disk.FileOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (string path in before.FileOverrides.Keys.Union(after.FileOverrides.Keys, StringComparer.OrdinalIgnoreCase))
        {
            PreviewStyleOverrides oldValue = before.GetOverride(path), newValue = after.GetOverride(path);
            // Future style features must be compared independently here too.
            if (oldValue.OrderedList != newValue.OrderedList)
            {
                PreviewStyleOverrides merged = disk.GetOverride(path) with { OrderedList = newValue.OrderedList };
                if (merged.IsEmpty) files.Remove(path);
                else files[path] = merged;
            }
        }
        return disk with { FileOverrides = files };
    }

    public PreviewStyleFileSettings Relocate(string source, string target)
    {
        string from = NormalizePath(source) ?? throw new ArgumentException("源路径无效。", nameof(source));
        string to = NormalizePath(target) ?? throw new ArgumentException("目标路径无效。", nameof(target));
        if (StringComparer.OrdinalIgnoreCase.Equals(from, to)) return this;
        Dictionary<string, PreviewStyleOverrides> files = new(FileOverrides, StringComparer.OrdinalIgnoreCase);
        List<(string Old, string New, PreviewStyleOverrides Style)> moved = [];
        foreach ((string path, PreviewStyleOverrides style) in FileOverrides)
        {
            string relative = Path.GetRelativePath(from, path);
            if (Path.IsPathFullyQualified(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) continue;
            moved.Add((path, relative == "." ? to : Path.Combine(to, relative), style));
        }
        foreach (var entry in moved) files.Remove(entry.Old);
        foreach (var entry in moved) files[entry.New] = entry.Style;
        return moved.Count == 0 ? this : this with { FileOverrides = files };
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return null; }
    }
}
