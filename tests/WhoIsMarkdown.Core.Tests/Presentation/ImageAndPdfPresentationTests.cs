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
        string imageCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Images.cs"));

        Assert.Contains("Header=\"导出为 PDF...\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"ExportPdf_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"InsertImage_Click\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InsertImageToolbarButton\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("*.webp;*.svg", imageCode, StringComparison.Ordinal);
        Assert.Contains("不会上传到 ImgBB", imageCode, StringComparison.Ordinal);

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
    public void 自定义背景_覆盖编辑与预览区域且可见度语义同向()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string appearanceCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Appearance.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));
        string backgroundWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "BackgroundSettingsWindow.xaml"));
        string backgroundWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "BackgroundSettingsWindow.xaml.cs"));
        string previewStyle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains(
            "AutomationProperties.Name=\"背景可见度\"",
            backgroundWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip=\"0% 完全隐藏，100% 最清晰\"",
            backgroundWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"EditorHost\" Grid.Column=\"0\" Background=\"{DynamicResource ShellOverlayBrush}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"PreviewHost\" Grid.Column=\"2\" Background=\"{DynamicResource ShellOverlayBrush}\"",
            mainWindow,
            StringComparison.Ordinal);
        XDocument xaml = XDocument.Parse(mainWindow);
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement editor = Assert.Single(
            xaml.Descendants(),
            element =>
                element.Name.LocalName == "TextEditor" &&
                (string?)element.Attribute(xNamespace + "Name") == "Editor");
        Assert.Equal("Transparent", (string?)editor.Attribute("Background"));
        Assert.DoesNotContain(
            "Background=\"#D9FFFFFF\"",
            mainWindow,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "<Border Background=\"#62F4F5FA\"",
            mainWindow,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "BackgroundAppearanceScale.FromPercentage(percentage)",
            appearanceCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "1 - (eventArgs.NewValue / 100)",
            appearanceCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "background: var(--wimd-document, rgba(255, 255, 255, 0.25));",
            previewStyle,
            StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundSettingsPopup", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("BackgroundSettingsPopup", appearanceCode, StringComparison.Ordinal);
        Assert.Contains(
            "BlurRadius=\"16\" Opacity=\"0.16\" ShadowDepth=\"3\"",
            backgroundWindow,
            StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", backgroundWindow, StringComparison.Ordinal);
        int selectHandlerStart = backgroundWindowCode.IndexOf(
            "private void SelectBackground_Click",
            StringComparison.Ordinal);
        int selectHandlerEnd = backgroundWindowCode.IndexOf(
            "private void RemoveBackground_Click",
            StringComparison.Ordinal);
        Assert.True(selectHandlerStart >= 0 && selectHandlerEnd > selectHandlerStart);
        string selectHandler = backgroundWindowCode[selectHandlerStart..selectHandlerEnd];
        Assert.Contains("dialog.ShowDialog(this)", selectHandler, StringComparison.Ordinal);
        Assert.Contains("BackgroundImageSelected?.Invoke(dialog.FileName)", selectHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("Close()", selectHandler, StringComparison.Ordinal);
        Assert.Contains("ReadComponentTextResource", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("\"preview.css\"", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains(
            "typeof(MainWindow).Assembly.GetName().Name",
            mainWindowCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 自定义背景_贯穿标题菜单侧栏工具栏和状态栏()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string chromeCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Chrome.cs"));
        string commandsCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Commands.cs"));

        XDocument xaml = XDocument.Parse(mainWindow);
        XNamespace xNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement window = xaml.Root ?? throw new InvalidOperationException("主窗口 XAML 缺少根元素。");
        Assert.Equal("None", (string?)window.Attribute("WindowStyle"));
        Assert.Equal("Window_SourceInitialized", (string?)window.Attribute("SourceInitialized"));
        Assert.Equal("True", (string?)window.Attribute("SnapsToDevicePixels"));
        Assert.Equal("True", (string?)window.Attribute("UseLayoutRounding"));
        Assert.Contains(
            xaml.Descendants(),
            element => element.Name.LocalName == "WindowChrome");

        XElement titleText = Assert.Single(
            xaml.Descendants(),
            element => (string?)element.Attribute("Text") == "{Binding WindowTitle}");
        Assert.Equal("Display", (string?)titleText.Attribute("TextOptions.TextFormattingMode"));
        Assert.Equal("Grayscale", (string?)titleText.Attribute("TextOptions.TextRenderingMode"));

        foreach (string elementName in new[]
                 {
                     "TitleBar",
                     "TopMenu",
                     "RecentPane",
                     "MarkdownToolbarHost",
                     "EditorHost",
                     "PreviewHost",
                     "StatusBar",
                 })
        {
            XElement surface = Assert.Single(
                xaml.Descendants(),
                element => (string?)element.Attribute(xNamespace + "Name") == elementName);
            Assert.Equal(
                "{DynamicResource ShellOverlayBrush}",
                (string?)surface.Attribute("Background"));
        }

        Assert.Contains("WindowState = WindowState.Minimized", chromeCode, StringComparison.Ordinal);
        Assert.Contains("WindowState.Maximized", chromeCode, StringComparison.Ordinal);
        Assert.Contains("Close();", chromeCode, StringComparison.Ordinal);
        Assert.Contains("WindowMessageGetMinMaxInfo", chromeCode, StringComparison.Ordinal);
        Assert.Contains("MonitorFromWindow", chromeCode, StringComparison.Ordinal);
        Assert.Contains("monitorInfo.WorkArea", chromeCode, StringComparison.Ordinal);
        Assert.Contains("DisposeWindowChrome();", commandsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void 自定义背景_代码块和表格使用分层半透明表面()
    {
        string repositoryRoot = FindRepositoryRoot();
        string previewStyle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains(
            "background: var(--wimd-code-bg, rgba(238, 241, 247, 0.72));",
            previewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "background: var(--wimd-table-cell-bg, rgba(255, 255, 255, 0.22));",
            previewStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "background: var(--wimd-table-heading-bg, rgba(230, 234, 243, 0.78));",
            previewStyle,
            StringComparison.Ordinal);
        Assert.DoesNotContain("background: #f7f8fb;", previewStyle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("background: #f4f5fa;", previewStyle, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("OnWebResourceRequested", previewCode, StringComparison.Ordinal);
        Assert.Contains("SafeSvgSanitizer.SanitizeFileAsync", previewCode, StringComparison.Ordinal);
        Assert.Contains("or InvalidOperationException", previewCode, StringComparison.Ordinal);
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
        Assert.Contains("pre::-webkit-scrollbar-thumb", previewStyle, StringComparison.Ordinal);
        Assert.Contains("border-radius: 999px;", previewStyle, StringComparison.Ordinal);
        Assert.Contains("pre::-webkit-scrollbar-button", previewStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollbar-width:", previewStyle, StringComparison.Ordinal);
        Assert.Matches(
            "pre::\\-webkit-scrollbar-button\\s*\\{[^}]*display:\\s*none;[^}]*width:\\s*0;[^}]*height:\\s*0;",
            previewStyle);
        Assert.Matches(
            "@media print[\\s\\S]*\\.wimd-code-copy-button\\s*\\{\\s*display: none;\\s*\\}",
            previewStyle);
    }

    [Fact]
    public void 预览任务_提供绿色完成状态和受校验的源码切换桥接()
    {
        string repositoryRoot = FindRepositoryRoot();
        string previewCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "PreviewWebViewService.cs"));
        string editorCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.Editing.cs"));
        string previewStyle = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains("toggle-task-list-item", previewCode, StringComparison.Ordinal);
        Assert.Contains("PreviewTaskToggleRequest.TryCreate", previewCode, StringComparison.Ordinal);
        Assert.Contains("window.wimdTaskToggle?.complete", previewCode, StringComparison.Ordinal);
        Assert.Contains("MarkdownTaskListService.TryCreateStateEdit", editorCode, StringComparison.Ordinal);
        Assert.Contains(".wimd-task-completed", previewStyle, StringComparison.Ordinal);
        Assert.Contains("#17835c", previewStyle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 表格工具_提供含表头行数与列数选择窗口()
    {
        string repositoryRoot = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml"));
        string dialog = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "TableSizeDialog.xaml"));
        string dialogCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "TableSizeDialog.xaml.cs"));

        Assert.Contains("Header=\"插入表格...\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"行数（含表头）\"", dialog, StringComparison.Ordinal);
        Assert.Contains("Text=\"列数\"", dialog, StringComparison.Ordinal);
        Assert.Contains("SelectedRowCount", dialogCode, StringComparison.Ordinal);
        Assert.Contains("SelectedColumnCount", dialogCode, StringComparison.Ordinal);
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
