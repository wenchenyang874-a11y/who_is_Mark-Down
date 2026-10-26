using System.Text.Json;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class HeadingFoldTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FoldReport_ValidNumericPayload_ReadsOnlyBoundedMetrics(bool userAction)
    {
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            elapsedMs = 2.5,
            collapsedCount = 3,
            userAction,
            ignoredText = "never persisted",
        }));
        Assert.True(PreviewWebViewService.TryReadHeadingFoldReport(payload.RootElement, out double milliseconds,
            out int count, out bool action));
        Assert.Equal(2.5, milliseconds);
        Assert.Equal(3, count);
        Assert.Equal(userAction, action);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"elapsedMs\":\"2\",\"collapsedCount\":1,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":-1,\"collapsedCount\":1,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":60001,\"collapsedCount\":1,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1e999,\"collapsedCount\":1,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1,\"collapsedCount\":-1,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1,\"collapsedCount\":1000001,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1,\"collapsedCount\":1.5,\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1,\"collapsedCount\":\"1\",\"userAction\":true}")]
    [InlineData("{\"elapsedMs\":1,\"collapsedCount\":1,\"userAction\":\"true\"}")]
    public void FoldReport_InvalidOrUnboundedPayload_IsIgnoredWithoutThrowing(string json)
    {
        using JsonDocument payload = JsonDocument.Parse(json);
        Assert.False(PreviewWebViewService.TryReadHeadingFoldReport(payload.RootElement, out _, out _, out _));
    }

    [Fact]
    public void FoldScript_HostOnlyInteraction_DoesNotStoreStateOutsidePageOrRewriteBody()
    {
        string script = HeadingFoldScript.Script;
        Assert.Contains("trustedButtons.has(button)", script, StringComparison.Ordinal);
        Assert.Contains("queueMicrotask", script, StringComparison.Ordinal);
        Assert.Contains("beforeprint", script, StringComparison.Ordinal);
        Assert.Contains("afterprint", script, StringComparison.Ordinal);
        Assert.Contains("wimd:mermaid-rendered", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("outerHTML", script, StringComparison.Ordinal);
    }
}
