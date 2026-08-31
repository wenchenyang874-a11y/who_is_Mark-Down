using System.Security.Cryptography;

namespace WhoIsMarkdown.Core.Tests.Presentation;

public sealed class MermaidPresentationTests
{
    private const string ExpectedBundleHash =
        "18327BEF70D96FB505FE7287D9F6A7362EBF07FF6576DDFAFFB1A06F3E1A2954";

    [Fact]
    public void Mermaid运行库_固定校验值并作为离线资源打包()
    {
        string repositoryRoot = FindRepositoryRoot();
        string bundlePath = Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "mermaid.min.js");
        string project = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "WhoIsMarkdown.App.csproj"));
        string notice = File.ReadAllText(Path.Combine(repositoryRoot, "THIRD-PARTY-NOTICES.md"));

        Assert.True(File.Exists(bundlePath));
        Assert.Equal(ExpectedBundleHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundlePath))));
        Assert.Contains("Resources\\mermaid.min.js", project, StringComparison.Ordinal);
        Assert.Contains("Mermaid 11.16.1", notice, StringComparison.Ordinal);
        Assert.Contains("The MIT License", notice, StringComparison.Ordinal);
        Assert.Contains(ExpectedBundleHash, notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Mermaid预览_严格离线渲染并隔离生成Svg()
    {
        string repositoryRoot = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "MermaidPreviewScript.cs"));
        string previewService = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Services",
            "PreviewWebViewService.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "MainWindow.xaml.cs"));
        string style = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "WhoIsMarkdown.App",
            "Resources",
            "preview.css"));

        Assert.Contains("securityLevel: 'strict'", script, StringComparison.Ordinal);
        Assert.Contains("suppressErrorRendering: true", script, StringComparison.Ordinal);
        Assert.Contains("maxTextSize: maximumSourceLength", script, StringComparison.Ordinal);
        Assert.Contains("maxEdges: 500", script, StringComparison.Ordinal);
        Assert.Contains("'themeCSS'", script, StringComparison.Ordinal);
        Assert.Contains("pre.mermaid", script, StringComparison.Ordinal);
        Assert.Contains("script, foreignObject, iframe, object, embed, image", script, StringComparison.Ordinal);
        Assert.Contains("data:image/svg+xml;base64", script, StringComparison.Ordinal);
        Assert.Contains("template.content.firstElementChild", script, StringComparison.Ordinal);
        Assert.Contains("svg instanceof SVGSVGElement", script, StringComparison.Ordinal);
        Assert.DoesNotContain("querySelector(':scope > svg')", script, StringComparison.Ordinal);
        Assert.Contains("wimd:preview-updated", script, StringComparison.Ordinal);
        Assert.Contains("wimd:mermaid-rendered", script, StringComparison.Ordinal);
        Assert.Contains("whenIdle", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bindFunctions", script, StringComparison.Ordinal);

        Assert.Contains("WaitForMermaidRenderingAsync", previewService, StringComparison.Ordinal);
        Assert.Contains("block.classList.contains('mermaid')", previewService, StringComparison.Ordinal);
        Assert.Contains("generatedDiagram", previewService, StringComparison.Ordinal);
        Assert.Contains("image.dataset.wimdGeneratedDiagram", previewService, StringComparison.Ordinal);
        Assert.Contains("\"mermaid.min.js\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains(".wimd-mermaid-surface", style, StringComparison.Ordinal);
        Assert.Contains(".wimd-mermaid-error", style, StringComparison.Ordinal);
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
