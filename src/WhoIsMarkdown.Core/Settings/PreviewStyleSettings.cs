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
/// Session-only overrides. A null feature follows its global default; resolving
/// must not mutate or serialize the override into the global preference store.
/// </summary>
public sealed record PreviewStyleOverrides
{
    public OrderedListStyle? OrderedList { get; init; }

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
