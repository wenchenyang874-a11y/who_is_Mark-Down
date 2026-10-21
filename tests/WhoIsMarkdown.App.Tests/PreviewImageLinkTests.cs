using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class PreviewImageLinkTests
{
    [Theory]
    [InlineData("https://example.com/project?a=1&b=2")]
    [InlineData("http://example.com/project")]
    [InlineData("mailto:example@example.com")]
    public void 图片目标解析_图片包含链接_分别保留图片地址和链接地址(string linkUri)
    {
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            source = "https://example.com/badge.svg",
            alternativeText = "badge",
            generatedDiagram = false,
            linkUri,
        }));

        object target = ParseTarget(payload.RootElement);

        Assert.Equal("https://example.com/badge.svg", ReadProperty(target, "Source"));
        Assert.Equal(linkUri, ReadProperty(target, "LinkUri"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"linkUri\":null}")]
    [InlineData("{\"linkUri\":\"\"}")]
    [InlineData("{\"linkUri\":\"  \"}")]
    [InlineData("{\"linkUri\":false}")]
    public void 图片目标解析_无有效链接字段_保留图片而不提供链接目标(string metadata)
    {
        JsonObject properties = JsonNode.Parse(metadata)!.AsObject();
        properties["source"] = "https://example.com/image.png";
        using JsonDocument payload = JsonDocument.Parse(properties.ToJsonString());

        object target = ParseTarget(payload.RootElement);

        Assert.Equal("https://example.com/image.png", ReadProperty(target, "Source"));
        Assert.Null(ReadProperty(target, "LinkUri"));
    }

    private static object ParseTarget(JsonElement payload) => typeof(PreviewWebViewService)
        .GetMethod("TryReadImageTarget", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [payload])!;

    private static object? ReadProperty(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target);
}
