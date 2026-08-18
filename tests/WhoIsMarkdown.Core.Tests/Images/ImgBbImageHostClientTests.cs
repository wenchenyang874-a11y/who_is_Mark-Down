using System.Net;
using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class ImgBbImageHostClientTests
{
    [Fact]
    public async Task 上传_接口返回可信地址_解析图片与删除链接()
    {
        RecordingHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("key=secret%2Bkey", request.RequestUri!.Query, StringComparison.Ordinal);
            Assert.IsType<MultipartFormDataContent>(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": {
                        "url": "https://i.ibb.co/demo/image.png",
                        "delete_url": "https://ibb.co/demo/delete-token"
                      },
                      "success": true,
                      "status": 200
                    }
                    """),
            };
        });
        using HttpClient httpClient = new(handler);
        using ImgBbImageHostClient client = new(httpClient);
        using MemoryStream image = new([1, 2, 3]);

        HostedImage result = await client.UploadAsync(
            image,
            "截图.png",
            "secret+key",
            TestContext.Current.CancellationToken);

        Assert.Equal("https://i.ibb.co/demo/image.png", result.Url.AbsoluteUri);
        Assert.Equal("https://ibb.co/demo/delete-token", result.DeleteUrl?.AbsoluteUri);
    }

    [Fact]
    public async Task 上传_返回非ImgBb图片域名_拒绝插入()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "data": { "url": "https://tracker.example/image.png" }, "success": true }
                """),
        });
        using HttpClient httpClient = new(handler);
        using ImgBbImageHostClient client = new(httpClient);
        using MemoryStream image = new([1]);

        ImageHostUploadException exception = await Assert.ThrowsAsync<ImageHostUploadException>(() =>
            client.UploadAsync(image, "image.png", "secret", TestContext.Current.CancellationToken));

        Assert.Contains("不受信任", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上传_网络异常包含请求地址_错误信息不泄露密钥()
    {
        const string secret = "do-not-leak-this-key";
        RecordingHandler handler = new(_ => throw new HttpRequestException(
            $"Request failed: https://api.imgbb.com/1/upload?key={secret}"));
        using HttpClient httpClient = new(handler);
        using ImgBbImageHostClient client = new(httpClient);
        using MemoryStream image = new([1, 2, 3]);

        ImageHostUploadException exception = await Assert.ThrowsAsync<ImageHostUploadException>(() =>
            client.UploadAsync(
                image,
                "image.png",
                secret,
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task 上传_图片超过32MB_发出请求前拒绝()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("不应发出请求"));
        using HttpClient httpClient = new(handler);
        using ImgBbImageHostClient client = new(httpClient);
        using LengthOnlyStream image = new(ImgBbImageHostClient.MaximumImageBytes + 1);

        await Assert.ThrowsAsync<ImageHostUploadException>(() =>
            client.UploadAsync(image, "image.png", "secret", TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
