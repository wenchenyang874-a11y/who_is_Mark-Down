using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Editing;

namespace WhoIsMarkdown.App;

public partial class TableSizeDialog : Window
{
    public TableSizeDialog()
    {
        InitializeComponent();
        RowCountComboBox.ItemsSource = Enumerable.Range(
            MarkdownFormattingService.MinimumTableRowCount,
            MarkdownFormattingService.MaximumTableRowCount
                - MarkdownFormattingService.MinimumTableRowCount
                + 1);
        ColumnCountComboBox.ItemsSource = Enumerable.Range(
            MarkdownFormattingService.MinimumTableColumnCount,
            MarkdownFormattingService.MaximumTableColumnCount
                - MarkdownFormattingService.MinimumTableColumnCount
                + 1);
        RowCountComboBox.SelectedItem = 3;
        ColumnCountComboBox.SelectedItem = 3;
        Loaded += (_, _) => RowCountComboBox.Focus();
        UpdateSummary();
    }

    public int SelectedRowCount => (int)RowCountComboBox.SelectedItem;

    public int SelectedColumnCount => (int)ColumnCountComboBox.SelectedItem;

    private void Size_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (RowCountComboBox.SelectedItem is not int rows
            || ColumnCountComboBox.SelectedItem is not int columns)
        {
            return;
        }

        SummaryText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "将插入 {0} 行 × {1} 列的表格",
            rows,
            columns);
    }

    private void Insert_Click(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }
}
