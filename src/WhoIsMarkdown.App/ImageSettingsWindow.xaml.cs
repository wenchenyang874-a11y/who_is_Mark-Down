using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

public partial class ImageSettingsWindow : Window
{
    private readonly ImageInsertionSettings initialSettings;
    private bool hasStoredApiKey;

    public ImageSettingsWindow(ImageInsertionSettings settings)
    {
        initialSettings = (settings ?? new ImageInsertionSettings()).Normalize();
        InitializeComponent();
        StorageModeComboBox.SelectedIndex = initialSettings.StorageMode == ImageStorageMode.ImgBb ? 1 : 0;
        LocalDirectoryTextBox.Text = initialSettings.LocalDirectory;
        hasStoredApiKey = !string.IsNullOrWhiteSpace(initialSettings.ProtectedImgBbApiKey);
        UpdateApiKeyStatus();
        UpdateVisibleSections();
    }

    internal ImageInsertionSettings ResultSettings { get; private set; } = new();
    internal string? NewApiKey { get; private set; }

    private void StorageMode_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
        => UpdateVisibleSections();

    private void ClearApiKey_Click(object sender, RoutedEventArgs eventArgs)
    {
        hasStoredApiKey = false;
        ApiKeyPasswordBox.Clear();
        UpdateApiKeyStatus();
    }

    private void OpenImgBbApiPage_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { Process.Start(new ProcessStartInfo("https://api.imgbb.com/") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(this, $"无法打开 ImgBB API 页面：{exception.Message}",
                "打开网页失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        string localDirectory;
        try { localDirectory = LocalImageStorageService.NormalizeRelativeDirectory(LocalDirectoryTextBox.Text); }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "图片设置有误",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string enteredApiKey = ApiKeyPasswordBox.Password.Trim();
        if (enteredApiKey.Length > 512)
        {
            MessageBox.Show(this, "ImgBB API Key 长度异常。", "图片设置有误");
            return;
        }
        ImageStorageMode storageMode = GetStorageMode();
        if (storageMode == ImageStorageMode.ImgBb && !hasStoredApiKey && enteredApiKey.Length == 0)
        {
            MessageBox.Show(this, "使用 ImgBB 上传模式前，请填写自己的 API Key。",
                "缺少 ImgBB API Key", MessageBoxButton.OK, MessageBoxImage.Information);
            ApiKeyPasswordBox.Focus();
            return;
        }

        NewApiKey = enteredApiKey.Length == 0 ? null : enteredApiKey;
        ResultSettings = initialSettings with
        {
            StorageMode = storageMode,
            LocalDirectory = localDirectory,
            TrustMode = RemoteImageTrustMode.TrustAll,
            RemoteImageRules = [],
            ProtectedImgBbApiKey = hasStoredApiKey ? initialSettings.ProtectedImgBbApiKey : null,
        };
        DialogResult = true;
    }

    private void UpdateVisibleSections()
    {
        if (LocalSettingsPanel is null || ImgBbSettingsPanel is null) return;
        bool usesImgBb = GetStorageMode() == ImageStorageMode.ImgBb;
        LocalSettingsPanel.Visibility = usesImgBb ? Visibility.Collapsed : Visibility.Visible;
        ImgBbSettingsPanel.Visibility = usesImgBb ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateApiKeyStatus()
    {
        ApiKeyStatusText.Text = hasStoredApiKey
            ? "已保存 API Key；输入新值可替换，留空则保留。" : "尚未保存 API Key。";
    }

    private ImageStorageMode GetStorageMode()
        => StorageModeComboBox?.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out ImageStorageMode mode) ? mode : ImageStorageMode.Local;
}
