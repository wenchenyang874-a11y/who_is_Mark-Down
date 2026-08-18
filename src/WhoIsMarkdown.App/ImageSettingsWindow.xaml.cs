using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

public partial class ImageSettingsWindow : Window
{
    private readonly ImageInsertionSettings initialSettings;
    private bool hasStoredApiKey;
    private readonly bool initializing = true;

    public ImageSettingsWindow(ImageInsertionSettings settings)
    {
        initialSettings = (settings ?? new ImageInsertionSettings()).Normalize();
        InitializeComponent();
        DataContext = this;

        StorageModeComboBox.SelectedIndex = initialSettings.StorageMode == ImageStorageMode.ImgBb ? 1 : 0;
        LocalDirectoryTextBox.Text = initialSettings.LocalDirectory;
        TrustModeComboBox.SelectedIndex = (int)initialSettings.TrustMode;
        hasStoredApiKey = !string.IsNullOrWhiteSpace(initialSettings.ProtectedImgBbApiKey);
        foreach (string rule in initialSettings.RemoteImageRules)
        {
            Rules.Add(ParseRule(rule));
        }

        initializing = false;
        UpdateApiKeyStatus();
        UpdateVisibleSections();
    }

    public ObservableCollection<RemoteImageRuleEditorItemViewModel> Rules { get; } = [];

    public IReadOnlyList<RemoteImageMatchOption> MatchOptions { get; } =
    [
        new("domain", "域名", "i.ibb.co"),
        new("prefix", "前缀", "https://cdn.example.com/"),
        new("suffix", "后缀", "/cover.png"),
        new("keyword", "关键词", "avatar"),
        new("regex", "正则表达式", "^https://images\\.example\\.com/"),
    ];

    internal ImageInsertionSettings ResultSettings { get; private set; } = new();

    internal string? NewApiKey { get; private set; }

    private void StorageMode_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (!initializing && GetStorageMode() == ImageStorageMode.ImgBb)
        {
            EnsureImgBbPreviewRule();
        }

        UpdateVisibleSections();
    }

    private void TrustMode_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        UpdateVisibleSections();
    }

    private void AddRule_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Rules.Count >= RemoteImagePolicy.MaximumRuleCount)
        {
            MessageBox.Show(
                this,
                $"最多只能添加 {RemoteImagePolicy.MaximumRuleCount} 条远程图片规则。",
                "规则数量已达上限",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Rules.Add(new RemoteImageRuleEditorItemViewModel("domain", string.Empty));
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is FrameworkElement { Tag: RemoteImageRuleEditorItemViewModel item })
        {
            Rules.Remove(item);
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs eventArgs)
    {
        hasStoredApiKey = false;
        ApiKeyPasswordBox.Clear();
        UpdateApiKeyStatus();
    }

    private void OpenImgBbApiPage_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://api.imgbb.com/") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(
                this,
                $"无法打开 ImgBB API 页面：{exception.Message}",
                "打开网页失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        ImageStorageMode storageMode = GetStorageMode();
        RemoteImageTrustMode trustMode = GetTrustMode();
        string localDirectory;
        IReadOnlyList<string> normalizedRules;
        try
        {
            localDirectory = LocalImageStorageService.NormalizeRelativeDirectory(
                LocalDirectoryTextBox.Text);
            normalizedRules = RemoteImagePolicy.NormalizeRules(
                Rules.Select(item => $"{item.KindId}:{item.Value}"));
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "图片设置有误",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string enteredApiKey = ApiKeyPasswordBox.Password.Trim();
        if (enteredApiKey.Length > 512)
        {
            MessageBox.Show(this, "ImgBB API Key 长度异常。", "图片设置有误");
            return;
        }

        if (storageMode == ImageStorageMode.ImgBb
            && !hasStoredApiKey
            && enteredApiKey.Length == 0)
        {
            MessageBox.Show(
                this,
                "使用 ImgBB 上传模式前，请填写自己的 API Key。",
                "缺少 ImgBB API Key",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ApiKeyPasswordBox.Focus();
            return;
        }

        if (trustMode == RemoteImageTrustMode.BlockList && normalizedRules.Count == 0)
        {
            MessageBoxResult result = MessageBox.Show(
                this,
                "黑名单当前为空，这等同于信任所有 HTTPS 远程图片。是否继续保存？",
                "空黑名单",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        NewApiKey = enteredApiKey.Length == 0 ? null : enteredApiKey;
        ResultSettings = initialSettings with
        {
            StorageMode = storageMode,
            LocalDirectory = localDirectory,
            TrustMode = trustMode,
            RemoteImageRules = normalizedRules,
            ProtectedImgBbApiKey = hasStoredApiKey
                ? initialSettings.ProtectedImgBbApiKey
                : null,
        };
        DialogResult = true;
    }

    private void EnsureImgBbPreviewRule()
    {
        if (GetTrustMode() != RemoteImageTrustMode.BlockAll)
        {
            return;
        }

        TrustModeComboBox.SelectedIndex = (int)RemoteImageTrustMode.AllowList;
        if (!Rules.Any(item => item.KindId == "domain"
            && item.Value.Equals(ImgBbImageHostClient.ImageHostName, StringComparison.OrdinalIgnoreCase)))
        {
            Rules.Add(new RemoteImageRuleEditorItemViewModel(
                "domain",
                ImgBbImageHostClient.ImageHostName));
        }
    }

    private void UpdateVisibleSections()
    {
        if (LocalSettingsPanel is null || RulesPanel is null)
        {
            return;
        }

        bool usesImgBb = GetStorageMode() == ImageStorageMode.ImgBb;
        LocalSettingsPanel.Visibility = usesImgBb ? Visibility.Collapsed : Visibility.Visible;
        ImgBbSettingsPanel.Visibility = usesImgBb ? Visibility.Visible : Visibility.Collapsed;

        RemoteImageTrustMode trustMode = GetTrustMode();
        bool usesRules = trustMode is RemoteImageTrustMode.AllowList or RemoteImageTrustMode.BlockList;
        RulesPanel.Visibility = usesRules ? Visibility.Visible : Visibility.Collapsed;
        RulesTitleText.Text = trustMode == RemoteImageTrustMode.BlockList ? "黑名单规则" : "白名单规则";
        TrustModeDescriptionText.Text = trustMode switch
        {
            RemoteImageTrustMode.AllowList => "只请求命中任意规则的 HTTPS 图片；这是兼顾可用性与隐私的推荐方式。",
            RemoteImageTrustMode.BlockList => "除命中规则的地址外都会请求；未知域名仍可联网，风险高于白名单。",
            RemoteImageTrustMode.TrustAll => "所有 HTTPS 图片都会自动请求，可能暴露 IP、访问时间和客户端信息。",
            _ => "远程图片会替换为本地占位内容，不产生图片网络请求。",
        };
    }

    private void UpdateApiKeyStatus()
    {
        ApiKeyStatusText.Text = hasStoredApiKey
            ? "已保存 API Key；输入新值可替换，留空则保留。"
            : "尚未保存 API Key。";
    }

    private ImageStorageMode GetStorageMode()
    {
        return StorageModeComboBox?.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out ImageStorageMode mode)
            ? mode
            : ImageStorageMode.Local;
    }

    private RemoteImageTrustMode GetTrustMode()
    {
        return TrustModeComboBox?.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out RemoteImageTrustMode mode)
            ? mode
            : RemoteImageTrustMode.BlockAll;
    }

    private static RemoteImageRuleEditorItemViewModel ParseRule(string rule)
    {
        int separator = rule.IndexOf(':');
        return separator > 0
            ? new RemoteImageRuleEditorItemViewModel(rule[..separator], rule[(separator + 1)..])
            : new RemoteImageRuleEditorItemViewModel("domain", rule);
    }
}
