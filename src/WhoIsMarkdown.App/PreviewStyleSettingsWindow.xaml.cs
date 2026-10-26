using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

public sealed record OrderedListStyleOption(string Label, OrderedListStyle? Value);

/// <summary>
/// Preview choices locally until Apply/OK. Applied choices survive Cancel, matching
/// the existing appearance dialog; merely browsing choices never changes settings.
/// </summary>
public partial class PreviewStyleSettingsWindow : Window
{
    private readonly PreviewStyleSettings initialDefaults;
    private readonly PreviewStyleOverrides initialOverrides;

    public PreviewStyleSettingsWindow(PreviewStyleSettings defaults, PreviewStyleOverrides overrides)
    {
        initialDefaults = defaults.Normalize();
        initialOverrides = overrides;
        InitializeComponent();
        OrderedListStyleOption[] styles =
        [
            new("原生简洁", OrderedListStyle.Plain),
            new("加粗强调", OrderedListStyle.Emphasis),
            new("圆形徽标", OrderedListStyle.Circle),
            new("圆角方标", OrderedListStyle.RoundedSquare),
        ];
        GlobalStyleComboBox.ItemsSource = styles;
        WindowStyleComboBox.ItemsSource = new[] { new OrderedListStyleOption("跟随全局默认", null) }.Concat(styles);
        GlobalStyleComboBox.SelectedItem = styles.FirstOrDefault(item => item.Value == initialDefaults.OrderedList) ?? styles[0];
        WindowStyleComboBox.SelectedItem = WindowStyleComboBox.Items.Cast<OrderedListStyleOption>()
            .FirstOrDefault(item => item.Value == overrides.OrderedList) ?? WindowStyleComboBox.Items[0];
    }

    public event Action<PreviewStyleSettings, PreviewStyleOverrides>? StyleApplied;

    private void Style_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (StylePreview is null || GlobalStyleComboBox.SelectedItem is not OrderedListStyleOption global
            || WindowStyleComboBox.SelectedItem is not OrderedListStyleOption local)
        {
            return;
        }

        OrderedListStyle style = local.Value ?? global.Value ?? OrderedListStyle.Plain;
        StylePreview.Children.Clear();
        AddPreviewRow("1", "第一项内容", style);
        AddPreviewRow("2", "第二项内容", style);
        AddPreviewRow("12", "多位数编号", style);
    }

    private void AddPreviewRow(string number, string text, OrderedListStyle style)
    {
        bool badge = style is OrderedListStyle.Circle or OrderedListStyle.RoundedSquare;
        TextBlock marker = new()
        {
            Text = badge ? number : number + ".",
            FontSize = 13,
            FontWeight = style == OrderedListStyle.Plain ? FontWeights.Normal : FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        marker.SetResourceReference(TextBlock.ForegroundProperty,
            style == OrderedListStyle.Plain ? "TextPrimaryBrush" : "AccentBrush");
        Border decoration = new()
        {
            Child = marker,
            MinWidth = 28,
            Height = 28,
            Padding = new Thickness(4, 0, 4, 0),
            CornerRadius = new CornerRadius(style == OrderedListStyle.Circle ? 999 : 6),
            Margin = new Thickness(0, 0, 12, 0),
        };
        decoration.SetResourceReference(Border.BackgroundProperty, badge ? "SelectionBrush" : "SurfaceMutedBrush");
        DockPanel row = new() { Margin = new Thickness(0, 3, 0, 3) };
        row.Children.Add(decoration);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        StylePreview.Children.Add(row);
    }

    private void Apply_Click(object sender, RoutedEventArgs eventArgs) => ApplySelection();

    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        ApplySelection();
        DialogResult = true;
    }

    private void ApplySelection()
    {
        if (GlobalStyleComboBox.SelectedItem is OrderedListStyleOption { Value: { } global }
            && WindowStyleComboBox.SelectedItem is OrderedListStyleOption local)
        {
            // Preserve other feature choices when this section edits one field.
            StyleApplied?.Invoke(initialDefaults with { OrderedList = global },
                initialOverrides with { OrderedList = local.Value });
        }
    }
}
