using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using WhoIsMarkdown.App.ViewModels;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

public partial class AppearanceSettingsWindow : Window
{
    private const string DefaultFontDisplayName = "跟随 WIMD 默认";
    private readonly IReadOnlyList<FontFamilyOption> fontOptions;
    private FontFamilyOption? selectedEditorFont;
    private FontFamilyOption? selectedPreviewFont;
    private bool updatingFontControls;

    public AppearanceSettingsWindow(AppearanceSettings settings)
    {
        InitializeComponent();
        AppearanceSettings normalized = (settings ?? new AppearanceSettings()).Normalize();
        fontOptions = CreateFontOptions();
        EditorFontComboBox.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(FontComboBox_TextChanged));
        PreviewFontComboBox.AddHandler(
            TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(FontComboBox_TextChanged));
        SetControls(normalized);
    }

    public AppearanceSettings ResultSettings { get; private set; } = new();

    public event Action<AppearanceSettings>? AppearanceApplied;

    private void Save_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryBuildSettings(out AppearanceSettings? settings) || settings is null)
        {
            return;
        }

        ResultSettings = settings;
        DialogResult = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!TryBuildSettings(out AppearanceSettings? settings) || settings is null)
        {
            return;
        }

        ResultSettings = settings;
        AppearanceApplied?.Invoke(settings);
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetControls(new AppearanceSettings());
    }

    private void FontSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (updatingFontControls)
        {
            return;
        }

        if (ReferenceEquals(sender, EditorFontComboBox)
            && EditorFontComboBox.SelectedItem is FontFamilyOption editorOption)
        {
            selectedEditorFont = editorOption;
            SetSelectedFontText(EditorFontComboBox, editorOption);
        }
        else if (ReferenceEquals(sender, PreviewFontComboBox)
            && PreviewFontComboBox.SelectedItem is FontFamilyOption previewOption)
        {
            selectedPreviewFont = previewOption;
            SetSelectedFontText(PreviewFontComboBox, previewOption);
        }

        UpdatePreview();
    }

    private void FontComboBox_TextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (updatingFontControls || sender is not ComboBox comboBox || !comboBox.IsKeyboardFocusWithin)
        {
            return;
        }

        string query = comboBox.Text.Trim();
        IReadOnlyList<FontFamilyOption> filtered = query.Length == 0
            ? fontOptions
            : fontOptions.Where(option => option.SearchText.Contains(
                query,
                StringComparison.CurrentCultureIgnoreCase)).ToArray();
        UpdateFilteredFontItems(comboBox, filtered, query);
    }

    private void UpdateFilteredFontItems(
        ComboBox comboBox,
        IReadOnlyList<FontFamilyOption> filtered,
        string query)
    {
        updatingFontControls = true;
        try
        {
            comboBox.ItemsSource = filtered;
            comboBox.SelectedItem = null;
            comboBox.Text = query;
        }
        finally
        {
            updatingFontControls = false;
        }

        // Restore the caret after ItemsSource replacement and keep the filtered
        // choices visible. Typing alone never commits a font; only item selection does.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox editor)
                {
                    editor.CaretIndex = editor.Text.Length;
                }

                comboBox.IsDropDownOpen = true;
            }));
    }

    private void SetControls(AppearanceSettings settings)
    {
        ThemeComboBox.SelectedValue = settings.Theme.ToString();
        selectedEditorFont = FindFont(settings.EditorFontFamily);
        selectedPreviewFont = FindFont(settings.PreviewFontFamily);
        SetSelectedFont(EditorFontComboBox, selectedEditorFont);
        SetSelectedFont(PreviewFontComboBox, selectedPreviewFont);
        EditorFontSizeComboBox.Text = settings.EditorFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        PreviewFontSizeComboBox.Text = settings.PreviewFontSize.ToString("0.##", CultureInfo.CurrentCulture);
        UpdatePreview();
    }

    private void SetSelectedFont(ComboBox comboBox, FontFamilyOption option)
    {
        updatingFontControls = true;
        try
        {
            comboBox.ItemsSource = fontOptions;
            comboBox.SelectedItem = option;
            comboBox.Text = option.DisplayName;
        }
        finally
        {
            updatingFontControls = false;
        }
    }

    private void SetSelectedFontText(ComboBox comboBox, FontFamilyOption option)
    {
        updatingFontControls = true;
        try
        {
            comboBox.Text = option.DisplayName;
        }
        finally
        {
            updatingFontControls = false;
        }
    }

    private bool TryBuildSettings(out AppearanceSettings? settings)
    {
        settings = null;
        if (!TryReadFontSize(EditorFontSizeComboBox, "编辑区", out double editorSize)
            || !TryReadFontSize(PreviewFontSizeComboBox, "预览区", out double previewSize))
        {
            return false;
        }

        settings = new AppearanceSettings
        {
            Theme = GetTheme(),
            EditorFontFamily = selectedEditorFont?.FamilyName,
            EditorFontSize = editorSize,
            PreviewFontFamily = selectedPreviewFont?.FamilyName,
            PreviewFontSize = previewSize,
        }.Normalize();
        return true;
    }

    private void UpdatePreview()
    {
        if (EditorFontPreviewText is null || PreviewFontPreviewText is null)
        {
            return;
        }

        string editorFamily = selectedEditorFont?.FamilyName ?? "Cascadia Mono, Consolas";
        string previewFamily = selectedPreviewFont?.FamilyName
            ?? "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI";
        EditorFontPreviewText.FontFamily = new FontFamily(editorFamily);
        PreviewFontPreviewText.FontFamily = new FontFamily(previewFamily);
        if (TryParseFontSize(EditorFontSizeComboBox.Text, out double editorSize))
        {
            EditorFontPreviewText.FontSize = editorSize;
        }

        if (TryParseFontSize(PreviewFontSizeComboBox.Text, out double previewSize))
        {
            PreviewFontPreviewText.FontSize = previewSize;
        }
    }

    private FontFamilyOption FindFont(string? familyName)
    {
        return fontOptions.FirstOrDefault(option => string.Equals(
            option.FamilyName,
            familyName,
            StringComparison.OrdinalIgnoreCase)) ?? fontOptions[0];
    }

    private ApplicationTheme GetTheme()
    {
        return ThemeComboBox.SelectedValue is string value
            && Enum.TryParse(value, out ApplicationTheme theme)
            ? theme
            : ApplicationTheme.System;
    }

    private bool TryReadFontSize(ComboBox comboBox, string area, out double value)
    {
        if (TryParseFontSize(comboBox.Text, out value))
        {
            return true;
        }

        MessageBox.Show(
            this,
            $"{area}字号必须为 {AppearanceSettings.MinimumFontSize:0} 到 {AppearanceSettings.MaximumFontSize:0} 之间的数字。",
            "字号无效",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        comboBox.Focus();
        return false;
    }

    private static bool TryParseFontSize(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
            && double.IsFinite(value)
            && value >= AppearanceSettings.MinimumFontSize
            && value <= AppearanceSettings.MaximumFontSize;
    }

    private static IReadOnlyList<FontFamilyOption> CreateFontOptions()
    {
        XmlLanguage chineseSimplified = XmlLanguage.GetLanguage("zh-CN");
        FontFamilyOption[] installed = Fonts.SystemFontFamilies
            .Where(font => !font.Source.StartsWith('@'))
            .Select(font => CreateFontOption(font, chineseSimplified))
            .Where(option => !string.IsNullOrWhiteSpace(option.FamilyName))
            .DistinctBy(option => option.FamilyName, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(option => option.IsChineseFont)
            .ThenBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return
        [
            new FontFamilyOption(DefaultFontDisplayName, null, "默认 system default", false),
            .. installed,
        ];
    }

    private static FontFamilyOption CreateFontOption(
        FontFamily family,
        XmlLanguage chineseSimplified)
    {
        string sourceName = family.Source;
        string? chineseName = family.FamilyNames.TryGetValue(chineseSimplified, out string? exactName)
            ? exactName
            : family.FamilyNames
                .FirstOrDefault(pair => pair.Key.IetfLanguageTag.StartsWith(
                    "zh",
                    StringComparison.OrdinalIgnoreCase))
                .Value;
        chineseName ??= GetKnownChineseDisplayName(sourceName);
        bool isChineseFont = !string.IsNullOrWhiteSpace(chineseName);
        string displayName = isChineseFont
            ? (string.Equals(chineseName, sourceName, StringComparison.OrdinalIgnoreCase)
                ? $"中文 · {sourceName}"
                : $"中文 · {chineseName} ({sourceName})")
            : sourceName;
        string aliases = string.Join(' ', family.FamilyNames.Values);
        string searchText = $"{displayName} {sourceName} {aliases} {(isChineseFont ? "中文 Chinese CJK" : string.Empty)}";
        return new FontFamilyOption(displayName, sourceName, searchText, isChineseFont);
    }

    private static string? GetKnownChineseDisplayName(string name)
    {
        (string Prefix, string DisplayName)[] families =
        [
            ("Microsoft YaHei UI", "微软雅黑 UI"),
            ("Microsoft YaHei", "微软雅黑"),
            ("Microsoft JhengHei", "微软正黑体"),
            ("NSimSun", "新宋体"),
            ("SimSun", "宋体"),
            ("SimHei", "黑体"),
            ("KaiTi", "楷体"),
            ("FangSong", "仿宋"),
            ("DengXian", "等线"),
        ];
        return families.FirstOrDefault(item => name.StartsWith(
            item.Prefix,
            StringComparison.OrdinalIgnoreCase)).DisplayName;
    }
}
