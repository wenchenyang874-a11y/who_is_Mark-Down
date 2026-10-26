using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>Display the exact distributed notices locally, without opening a website.</summary>
internal sealed class FontLicenseWindow : Window, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private readonly TextBox licenseText = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 13,
        Text = "正在读取本地许可证…",
        Padding = new Thickness(18),
    };

    public FontLicenseWindow()
    {
        Title = "内置字体 · 许可证与来源";
        Width = 720; Height = 620; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        licenseText.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        licenseText.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        Content = licenseText;
        Loaded += LoadLicenses;
        Closed += (_, _) => Dispose();
    }

    private async void LoadLicenses(object sender, RoutedEventArgs eventArgs)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Fonts");
        CancellationToken token = lifetime.Token;
        try
        {
            string content = await Task.Run(() => string.Join("\n\n",
                new[] { File.ReadAllText(Path.Combine(root, "README.md")) }.Concat(
                    BundledFontCatalog.Fonts.Select(font => $"=== {font.DisplayName} ===\n"
                        + File.ReadAllText(Path.Combine(root, font.LicenseDirectory, "OFL.txt"))
                        + ReadAuthors(root, font.LicenseDirectory))))).WaitAsync(token);
            if (IsVisible) licenseText.Text = content;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (IsVisible) licenseText.Text = $"无法读取本地字体许可证：{exception.Message}";
        }
    }

    private static string ReadAuthors(string root, string directory)
    {
        string path = Path.Combine(root, directory, "AUTHORS.txt");
        return File.Exists(path) ? "\n\n" + File.ReadAllText(path) : string.Empty;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
