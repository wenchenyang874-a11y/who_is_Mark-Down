using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class ReadmePresentationTests
{
    [Fact]
    public void RenderBody_CurrentReadme_ResolvesAllShowcaseImagesLocally()
    {
        string repositoryRoot = FindRepositoryRoot();
        string readmePath = Path.Combine(repositoryRoot, "README.md");
        string markdown = File.ReadAllText(readmePath);

        string html = new MarkdownRenderer().RenderBody(markdown, readmePath);

        Assert.Contains("<div align=\"center\">", html, StringComparison.Ordinal);
        Assert.Contains(
            "https://wimd-document.invalid/assets/app-icon.png",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://wimd-document.invalid/assets/screenshots/wimd-basic-demo.gif",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://wimd-document.invalid/assets/screenshots/wimd-interface.png",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "https://wimd-document.invalid/assets/screenshots/wimd-shell-open.png",
            html,
            StringComparison.Ordinal);
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
