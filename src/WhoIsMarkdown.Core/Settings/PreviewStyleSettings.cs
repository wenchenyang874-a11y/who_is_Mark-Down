namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Persisted preview defaults, grouped by feature. Add future table/code/heading
/// choices here and merge them independently, never as one replacement group.
/// </summary>
public sealed record PreviewStyleSettings
{
    public OrderedListStyle OrderedList { get; init; } = OrderedListStyle.Plain;

    public PreviewStyleSettings Normalize() => this with
    {
        OrderedList = Enum.IsDefined(OrderedList) ? OrderedList : OrderedListStyle.Plain,
    };
}

/// <summary>
/// File-scoped feature overrides. Null follows the global default; keep these
/// separate from defaults so one file's choice cannot affect unrelated files.
/// </summary>
public sealed record PreviewStyleOverrides
{
    public OrderedListStyle? OrderedList { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => OrderedList is null;

    public PreviewStyleOverrides Normalize() => this with
    {
        OrderedList = OrderedList is { } style && Enum.IsDefined(style) ? style : null,
    };

    public PreviewStyleSettings Resolve(PreviewStyleSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        PreviewStyleSettings normalized = defaults.Normalize();
        return normalized with
        {
            OrderedList = OrderedList is { } style && Enum.IsDefined(style) ? style : normalized.OrderedList,
        };
    }
}
