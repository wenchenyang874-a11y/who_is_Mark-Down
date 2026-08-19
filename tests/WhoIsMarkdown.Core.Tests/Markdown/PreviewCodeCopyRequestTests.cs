using System.Text.Json;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class PreviewCodeCopyRequestTests
{
    [Fact]
    public void TryCreate_多行代码_保留内容并接受请求()
    {
        using JsonDocument message = JsonDocument.Parse("""
            {"requestId":7,"code":"line 1\nline 2"}
            """);

        bool succeeded = PreviewCodeCopyRequest.TryCreate(
            message.RootElement,
            out PreviewCodeCopyRequest? request);

        Assert.True(succeeded);
        Assert.NotNull(request);
        Assert.Equal(7, request.RequestId);
        Assert.Equal("line 1\nline 2", request.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"requestId\":0,\"code\":\"text\"}")]
    [InlineData("{\"requestId\":1,\"code\":\"\"}")]
    [InlineData("{\"requestId\":1,\"code\":\"   \"}")]
    [InlineData("{\"requestId\":1,\"code\":5}")]
    public void TryCreate_无效消息_拒绝请求(string json)
    {
        using JsonDocument message = JsonDocument.Parse(json);

        bool succeeded = PreviewCodeCopyRequest.TryCreate(
            message.RootElement,
            out PreviewCodeCopyRequest? request);

        Assert.False(succeeded);
        Assert.Null(request);
    }

    [Fact]
    public void TryCreate_代码超过上限_拒绝请求()
    {
        string code = new('x', PreviewCodeCopyRequest.MaximumCodeLength + 1);
        using JsonDocument message = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            requestId = 1,
            code,
        }));

        bool succeeded = PreviewCodeCopyRequest.TryCreate(
            message.RootElement,
            out PreviewCodeCopyRequest? request);

        Assert.False(succeeded);
        Assert.Null(request);
    }
}
