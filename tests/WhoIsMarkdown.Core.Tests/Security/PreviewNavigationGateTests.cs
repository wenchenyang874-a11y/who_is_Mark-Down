using WhoIsMarkdown.Core.Security;

namespace WhoIsMarkdown.Core.Tests.Security;

public sealed class PreviewNavigationGateTests
{
    [Theory]
    [InlineData("about:blank")]
    [InlineData("data:text/html;charset=utf-8;base64,PGgxPkhlbGxvPC9oMT4=")]
    public void TryAllowGeneratedNavigation_AfterHostBeginsPreview_AllowsOneInternalNavigation(
        string previewUri)
    {
        PreviewNavigationGate gate = new();
        gate.BeginGeneratedNavigation();

        Assert.True(gate.TryAllowGeneratedNavigation(previewUri));
        Assert.False(gate.TryAllowGeneratedNavigation(previewUri));
    }

    [Theory]
    [InlineData("data:text/html,<h1>document link</h1>")]
    [InlineData("https://example.com/")]
    [InlineData("file:///C:/private.txt")]
    public void TryAllowGeneratedNavigation_WithoutPendingHostNavigation_BlocksUri(string uri)
    {
        PreviewNavigationGate gate = new();

        Assert.False(gate.TryAllowGeneratedNavigation(uri));
    }

    [Fact]
    public void TryAllowGeneratedNavigation_WhenPendingUriIsExternal_ConsumesPermissionAndBlocksIt()
    {
        PreviewNavigationGate gate = new();
        gate.BeginGeneratedNavigation();

        Assert.False(gate.TryAllowGeneratedNavigation("https://example.com/"));
        Assert.False(gate.TryAllowGeneratedNavigation("about:blank"));
    }
}
