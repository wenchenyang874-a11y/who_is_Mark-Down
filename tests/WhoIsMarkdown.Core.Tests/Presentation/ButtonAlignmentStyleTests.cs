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
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, "WhoIsMarkdown.sln");
            if (File.Exists(solutionPath))
            {
                return Path.Combine(directory.FullName, "src", "WhoIsMarkdown.App", "App.xaml");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
