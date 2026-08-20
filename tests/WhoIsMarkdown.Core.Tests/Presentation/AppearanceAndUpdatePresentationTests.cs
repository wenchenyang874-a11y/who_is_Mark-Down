using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class AppearanceAndUpdatePresentationTests
{
    [Fact]
    public void 字体选择_下拉框本身可输入筛选且不增加独立搜索框()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dialogXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml"));
        string dialogCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml.cs"));

        XDocument document = XDocument.Parse(dialogXaml);
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement[] fontSelectors = document.Descendants()
            .Where(element => element.Name.LocalName == "ComboBox")
            .Where(element =>
                (string?)element.Attribute(xNamespace + "Name") is "EditorFontComboBox"
                    or "PreviewFontComboBox")
            .ToArray();

        Assert.Equal(2, fontSelectors.Length);
        Assert.All(fontSelectors, selector =>
        {
            Assert.Equal("True", (string?)selector.Attribute("IsEditable"));
            Assert.Equal("False", (string?)selector.Attribute("IsTextSearchEnabled"));
            Assert.Equal("True", (string?)selector.Attribute("StaysOpenOnEdit"));
            Assert.Equal("DisplayName", (string?)selector.Attribute("DisplayMemberPath"));
        });
        Assert.DoesNotContain(
            document.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && ((string?)element.Attribute(xNamespace + "Name"))?.Contains(
                    "Search",
                    StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains("TextBoxBase.TextChangedEvent", dialogCode, StringComparison.Ordinal);
        Assert.Contains("option.SearchText.Contains", dialogCode, StringComparison.Ordinal);
        Assert.Contains("comboBox.IsDropDownOpen = true", dialogCode, StringComparison.Ordinal);
        Assert.Contains("only item selection does", dialogCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 字体选择_枚举已安装字体并提供常用中文名称()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dialogCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml.cs"));
        string dialogXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml"));

        Assert.Contains("Fonts.SystemFontFamilies", dialogCode, StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei", dialogCode, StringComparison.Ordinal);
        Assert.Contains("微软雅黑", dialogCode, StringComparison.Ordinal);
        Assert.Contains("SimSun", dialogCode, StringComparison.Ordinal);
        Assert.Contains("宋体", dialogCode, StringComparison.Ordinal);
        Assert.Contains("只引用本机已安装字体", dialogXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void 外观设置_应用按钮不关闭对话框并立即传递设置()
    {
        string repositoryRoot = FindRepositoryRoot();
        string dialogXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml"));
        string dialogCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "AppearanceSettingsWindow.xaml.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Appearance.cs"));

        Assert.Contains("Content=\"应用\" Click=\"Apply_Click\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("AppearanceApplied?.Invoke(settings)", dialogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DialogResult = true;\n        AppearanceApplied", dialogCode, StringComparison.Ordinal);
        Assert.Contains("dialog.AppearanceApplied += ApplyAppearanceFromDialog", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("dialog.AppearanceApplied -= ApplyAppearanceFromDialog", mainWindowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 背景设置_关闭按钮使用完整可见的通用字符()
    {
        string repositoryRoot = FindRepositoryRoot();
        string backgroundWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "BackgroundSettingsWindow.xaml"));

        Assert.Contains("Content=\"×\"", backgroundWindow, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Segoe UI, Microsoft YaHei UI\"", backgroundWindow, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Center\"", backgroundWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("&#xE8BB;", backgroundWindow, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WhoIsMarkdown.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
