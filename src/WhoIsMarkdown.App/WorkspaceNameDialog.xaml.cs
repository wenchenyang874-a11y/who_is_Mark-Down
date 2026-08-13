using System.Windows;
using System.Windows.Input;

namespace WhoIsMarkdown.App;

public partial class WorkspaceNameDialog : Window
{
    public WorkspaceNameDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameTextBox.Text = initialValue;
        NameTextBox.SelectAll();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string EnteredName => NameTextBox.Text;

    private void Confirm_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            MessageBox.Show(
                this,
                "名称不能为空。",
                "请输入名称",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            NameTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void NameTextBox_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
