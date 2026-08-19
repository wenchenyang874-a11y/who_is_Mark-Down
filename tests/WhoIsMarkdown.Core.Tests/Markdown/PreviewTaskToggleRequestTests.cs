using System.Text.Json;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewTaskToggleRequestTests
{
    [Fact]
    public void TryCreate_ValidHostMessage_ReturnsValidatedRequest()
    {
        using JsonDocument message = JsonDocument.Parse(
            """{"requestId":7,"sourceLine":12,"completed":true}""");

        bool succeeded = PreviewTaskToggleRequest.TryCreate(
            message.RootElement,
            out PreviewTaskToggleRequest? request);

        Assert.True(succeeded);
        Assert.Equal(new PreviewTaskToggleRequest(7, 12, true), request);
    }

    [Theory]
    [InlineData("{\"requestId\":0,\"sourceLine\":1,\"completed\":true}")]
    [InlineData("{\"requestId\":1,\"sourceLine\":-1,\"completed\":true}")]
    [InlineData("{\"requestId\":1,\"sourceLine\":10000001,\"completed\":true}")]
    [InlineData("{\"requestId\":1,\"sourceLine\":1,\"completed\":\"true\"}")]
    [InlineData("{\"requestId\":1,\"sourceLine\":1}")]
    public void TryCreate_InvalidHostMessage_RejectsRequest(string json)
    {
        using JsonDocument message = JsonDocument.Parse(json);

        bool succeeded = PreviewTaskToggleRequest.TryCreate(message.RootElement, out _);

        Assert.False(succeeded);
    }
}
