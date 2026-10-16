using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class RenderStageCancellationTests
{
    [Theory]
    [InlineData("first\n\nsecond")]
    [InlineData("<div>raw safe html</div>")]
    public void Render_CanceledAtImageRewrite_DoesNotReturnOldFilteredResult(string markdown)
    {
        using CancellationTokenSource cancellation = new();
        MarkdownRenderer renderer = new(new CancelingResolver(cancellation));
        Assert.Throws<OperationCanceledException>(() => renderer.RenderDocument(markdown, null, null, cancellation.Token));
    }

    [Fact]
    public void Packaging_AlreadyCanceled_StopsEvenForEmptyDocument()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MarkdownRenderResult.Create([], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => PreviewUpdateScriptBuilder.BuildPatch([], [], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new MarkdownRenderer().RenderDocument("unused", null, null, cancellation.Token));
    }

    private sealed class CancelingResolver(CancellationTokenSource cancellation) : ILocalImageUrlResolver
    {
        public string RewriteGeneratedHtml(string bodyHtml, string? documentPath, RemoteImagePolicy? remoteImagePolicy = null)
        {
            cancellation.Cancel();
            return bodyHtml;
        }
    }
}
