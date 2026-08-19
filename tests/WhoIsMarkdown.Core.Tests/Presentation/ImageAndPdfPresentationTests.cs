using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class ImageAndPdfPresentationTests
{
    [Fact]
    public void 主窗口_图片与Pdf功能_在菜单和工具栏中可发现()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));

        Assert.Contains("Header=\"导出为 PDF...\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"ExportPdf_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"InsertImage_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InsertImageToolbarButton\"", mainWindow, StringComparison.Ordinal);

        XDocument document = XDocument.Parse(mainWindow);
        XElement settingsMenu = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "MenuItem" &&
                (string?)element.Attribute("Header") == "设置(_S)");
        Assert.Equal("设置", (string?)settingsMenu.Attribute("AutomationProperties.Name"));
        Assert.Contains(settingsMenu.Elements(), element =>
            (string?)element.Attribute("Header") == "图片设置..." &&
            (string?)element.Attribute("Click") == "ImageSettings_Click");
        Assert.Contains(settingsMenu.Elements(), element =>
            (string?)element.Attribute("Header") == "自定义背景..." &&
            (string?)element.Attribute("Click") == "BackgroundSettings_Click");
    }

    [Fact]
    public void 图片设置_远程规则行_提供匹配方式下拉内容和删除操作()
    {
        string repositoryRoot = FindRepositoryRoot();
        string settingsWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ImageSettingsWindow.xaml"));
        string settingsCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ImageSettingsWindow.xaml.cs"));
        string ruleViewModel = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ViewModels",
            "RemoteImageRuleEditorItemViewModel.cs"));

        Assert.Contains("Content=\"添加规则\"", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("SelectedValuePath=\"Id\"", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"删除\"", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("不信任 - 阻止全部远程图片", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("信任所有 - 加载任意 HTTPS 图片", settingsWindow, StringComparison.Ordinal);
        Assert.Contains("public ObservableCollection<RemoteImageRuleEditorItemViewModel> Rules", settingsCode, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<RemoteImageMatchOption> MatchOptions", settingsCode, StringComparison.Ordinal);
        Assert.Contains("public sealed class RemoteImageRuleEditorItemViewModel", ruleViewModel, StringComparison.Ordinal);
        Assert.Contains("public sealed record RemoteImageMatchOption", ruleViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf导出_使用临时文件和WebView打印_避免覆盖半成品()
    {
        string repositoryRoot = FindRepositoryRoot();
        string exportCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Export.cs"));
        string previewCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "PreviewWebViewService.cs"));

        Assert.Contains(".tmp.pdf", exportCode, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, targetPath, overwrite: true)", exportCode, StringComparison.Ordinal);
        Assert.Contains("PrintToPdfAsync", previewCode, StringComparison.Ordinal);
        Assert.Contains("WaitUntilReadyAsync", previewCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览图片_使用独立窗口并提供缩放拖拽与另存()
    {
        string repositoryRoot = FindRepositoryRoot();
        string previewCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "PreviewWebViewService.cs"));
        string previewImageCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.PreviewImages.cs"));
        string viewerXaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "PreviewImageWindow.xaml"));
        string viewerCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "PreviewImageWindow.xaml.cs"));
        string previewStyle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains("open-preview-image", previewCode, StringComparison.Ordinal);
        Assert.Contains("PreviewImageOpenRequested", previewCode, StringComparison.Ordinal);
        Assert.Contains("image.draggable = false", previewCode, StringComparison.Ordinal);
        Assert.Contains("ResizeMode=\"CanResize\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"Fit_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ActualSize_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveAs_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("addEventListener('wheel'", viewerCode, StringComparison.Ordinal);
        Assert.Contains("addEventListener('pointerdown'", viewerCode, StringComparison.Ordinal);
        Assert.Contains("setPointerCapture", viewerCode, StringComparison.Ordinal);
        Assert.Contains("addEventListener('dragstart'", viewerCode, StringComparison.Ordinal);
        Assert.Contains("viewer.Show();", previewImageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("viewer.ShowDialog()", previewImageCode, StringComparison.Ordinal);
        Assert.Contains("Title = \"预览图片另存为\"", previewImageCode, StringComparison.Ordinal);
        Assert.Contains("PreviewImageSaveService", previewImageCode, StringComparison.Ordinal);
        Assert.DoesNotContain("preview-image-lightbox", previewStyle, StringComparison.Ordinal);
        Assert.Contains("cursor: zoom-in", previewStyle, StringComparison.Ordinal);
    }

    [Fact]
    public void 预览代码块_提供宿主剪贴板按钮且打印时隐藏()
    {
        string repositoryRoot = FindRepositoryRoot();
        string previewCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "PreviewWebViewService.cs"));
        string previewStyle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains("copy-code-block", previewCode, StringComparison.Ordinal);
        Assert.Contains("wimd:preview-updated", previewCode, StringComparison.Ordinal);
        Assert.Contains("PreviewCodeCopyRequest.TryCreate", previewCode, StringComparison.Ordinal);
        Assert.Contains("clipboardTextService.TrySetTextAsync", previewCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdCodeCopy?.complete", previewCode, StringComparison.Ordinal);
        Assert.Contains("block.before(container)", previewCode, StringComparison.Ordinal);
        Assert.Contains("rawCode.replace(/\\r?\\n$/, '')", previewCode, StringComparison.Ordinal);
        Assert.Contains(".wimd-code-copy-button", previewStyle, StringComparison.Ordinal);
        Assert.Contains(".wimd-code-block pre", previewStyle, StringComparison.Ordinal);
        Assert.Matches(
            "@media print[\\s\\S]*\\.wimd-code-copy-button\\s*\\{\\s*display: none;\\s*\\}",
            previewStyle);
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
