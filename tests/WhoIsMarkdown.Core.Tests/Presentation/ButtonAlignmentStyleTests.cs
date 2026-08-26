using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Tests.Presentation;

/// <summary>
/// Regression contract for the shared WPF button template. The template previously
/// hard-coded centered content, so the recent-file style could not left-align short names.
/// </summary>
public sealed class ButtonAlignmentStyleTests
{
    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SharedButtonTemplate_RecentFileStyle_UsesRequestedLeftAlignment()
    {
        XDocument document = XDocument.Load(GetAppXamlPath());
        XElement toolbarStyle = FindStyle(document, "ToolbarButtonStyle");
        XElement recentFileStyle = FindStyle(document, "RecentOpenButtonStyle");
        XElement presenter = Assert.Single(
            toolbarStyle.Descendants(PresentationNamespace + "ContentPresenter"));

        Assert.Equal("Center", GetSetterValue(toolbarStyle, "HorizontalContentAlignment"));
        Assert.Equal("Center", GetSetterValue(toolbarStyle, "VerticalContentAlignment"));
        Assert.Equal(
            "{TemplateBinding HorizontalContentAlignment}",
            (string?)presenter.Attribute("HorizontalAlignment"));
        Assert.Equal(
            "{TemplateBinding VerticalContentAlignment}",
            (string?)presenter.Attribute("VerticalAlignment"));
        Assert.Equal("Stretch", GetSetterValue(recentFileStyle, "HorizontalContentAlignment"));
    }

    [Fact]
    public void RecentFileStyle_CurrentDocument_UsesThemeAwareHighlightAndTracksDocumentPath()
    {
        XDocument document = XDocument.Load(GetAppXamlPath());
        XElement recentFileStyle = FindStyle(document, "RecentOpenButtonStyle");
        XElement currentTrigger = recentFileStyle
            .Descendants(PresentationNamespace + "DataTrigger")
            .Single(element => string.Equals(
                (string?)element.Attribute("Binding"),
                "{Binding IsCurrent}",
                StringComparison.Ordinal));

        Assert.Equal("True", (string?)currentTrigger.Attribute("Value"));
        Assert.Equal(
            "{DynamicResource SelectionBrush}",
            GetSetterValue(currentTrigger, "Background"));
        Assert.Equal(
            "{DynamicResource AccentBrush}",
            GetSetterValue(currentTrigger, "BorderBrush"));

        string repositoryRoot = GetRepositoryRoot();
        string recentFilesCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.RecentFiles.cs"));
        string itemViewModelCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "ViewModels",
            "RecentFileItemViewModel.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "new RecentFileItemViewModel(entry, document.FilePath)",
            recentFilesCode,
            StringComparison.Ordinal);
        Assert.Contains("public bool IsCurrent { get; }", itemViewModelCode, StringComparison.Ordinal);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", itemViewModelCode, StringComparison.Ordinal);
        string normalizedMainWindowCode = mainWindowCode.ReplaceLineEndings("\n");
        Assert.Contains(
            "document.StartNew(++untitledCounter);\n        ApplyDocumentToEditor();\n        RefreshRecentFilesView();",
            normalizedMainWindowCode,
            StringComparison.Ordinal);
    }

    private static XElement FindStyle(XDocument document, string key)
    {
        return document
            .Descendants(PresentationNamespace + "Style")
            .Single(element => string.Equals(
                (string?)element.Attribute(XamlNamespace + "Key"),
                key,
                StringComparison.Ordinal));
    }

    private static string? GetSetterValue(XElement style, string property)
    {
        return style
            .Elements(PresentationNamespace + "Setter")
            .Where(element => string.Equals(
                (string?)element.Attribute("Property"),
                property,
                StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Value"))
            .Single();
    }

    private static string GetAppXamlPath()
    {
        return Path.Combine(GetRepositoryRoot(), "src", "WhoIsMarkdown.App", "App.xaml");
    }

    private static string GetRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, "WhoIsMarkdown.sln");
            if (File.Exists(solutionPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
