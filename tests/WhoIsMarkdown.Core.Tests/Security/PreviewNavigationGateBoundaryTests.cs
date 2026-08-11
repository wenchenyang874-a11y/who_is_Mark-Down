using WhoIsMarkdown.Core.Security;

namespace WhoIsMarkdown.Core.Tests.Security;

public sealed class PreviewNavigationGateBoundaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAllowGeneratedNavigation_WhenUriIsEmpty_BlocksAndConsumesPermission(string uri)
    {
        PreviewNavigationGate gate = new();
        gate.BeginGeneratedNavigation();

        Assert.False(gate.TryAllowGeneratedNavigation(uri));
        Assert.False(gate.TryAllowGeneratedNavigation("about:blank"));
    }

    [Fact]
    public void CancelGeneratedNavigation_WhenPermissionWasPending_RevokesIt()
    {
        PreviewNavigationGate gate = new();
        gate.BeginGeneratedNavigation();
        gate.CancelGeneratedNavigation();

        Assert.False(gate.TryAllowGeneratedNavigation("about:blank"));
    }
}
