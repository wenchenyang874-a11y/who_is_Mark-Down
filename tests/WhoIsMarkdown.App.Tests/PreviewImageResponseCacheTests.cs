using System.IO;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class PreviewImageResponseCacheTests
{
    [Fact]
    public async Task 响应缓存_重复读取_复用相同字节且不重复获取响应()
    {
        using PreviewImageResponseCache cache = new();
        int reads = 0;
        await cache.CaptureAsync("https://example.com/image", "image/png", 4, () =>
        {
            reads++;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4]));
        });
        CachedPreviewImage first = (await cache.TryReadAsync("https://example.com/image", TestContext.Current.CancellationToken))!;
        CachedPreviewImage second = (await cache.TryReadAsync("https://example.com/image", TestContext.Current.CancellationToken))!;
        Assert.Same(first, second);
        Assert.Equal(1, reads);
        Assert.Equal(4, cache.RetainedBytes);
    }

    [Fact]
    public async Task 响应缓存_重定向_原始地址和最终地址共享图片()
    {
        using PreviewImageResponseCache cache = new();
        cache.RememberRedirect("https://example.com/start", "https://example.com/final");
        await cache.CaptureAsync("https://example.com/final", "image/svg+xml", 2,
            () => Task.FromResult<Stream>(new MemoryStream([1, 2])));
        Assert.Same(await cache.TryReadAsync("https://example.com/start", TestContext.Current.CancellationToken),
            await cache.TryReadAsync("https://example.com/final", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 响应缓存_图片URL包含片段_复用不含片段的原始HTTP响应()
    {
        using PreviewImageResponseCache cache = new();
        await cache.CaptureAsync("https://example.com/image.svg", "image/svg+xml", 1,
            () => Task.FromResult<Stream>(new MemoryStream([1])));
        Assert.NotNull(await cache.TryReadAsync("https://example.com/image.svg#view", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 响应缓存_超过容量_按最近使用淘汰且不超出总字节数()
    {
        using PreviewImageResponseCache cache = new(maximumBytes: 16, maximumEntries: 2);
        foreach (string source in new[] { "one", "two" })
            await cache.CaptureAsync(source, "image/png", 8, () => Task.FromResult<Stream>(new MemoryStream(new byte[8])));
        Assert.NotNull(await cache.TryReadAsync("one", TestContext.Current.CancellationToken));
        await cache.CaptureAsync("three", "image/png", 8, () => Task.FromResult<Stream>(new MemoryStream(new byte[8])));
        Assert.Null(await cache.TryReadAsync("two", TestContext.Current.CancellationToken));
        Assert.NotNull(await cache.TryReadAsync("one", TestContext.Current.CancellationToken));
        Assert.Equal(16, cache.RetainedBytes);
        await cache.CaptureAsync("oversized", "image/png", 17, () => throw new InvalidOperationException("Must not acquire oversized response."));
        Assert.Null(await cache.TryReadAsync("oversized", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 响应缓存_捕获失败_不改变图片加载且可以重试()
    {
        using PreviewImageResponseCache cache = new();
        await cache.CaptureAsync("image", "image/png", null, () => Task.FromException<Stream>(new IOException("synthetic")));
        Assert.Null(await cache.TryReadAsync("image", TestContext.Current.CancellationToken));
        await cache.CaptureAsync("image", "image/png", 1, () => Task.FromResult<Stream>(new MemoryStream([7])));
        Assert.Equal(7, (await cache.TryReadAsync("image", TestContext.Current.CancellationToken))!.Bytes[0]);
    }

    [Fact]
    public async Task 响应缓存_清除时原生请求未结束_拒绝迟到结果并释放迟到流()
    {
        using PreviewImageResponseCache cache = new();
        TaskCompletionSource<Stream> native = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task capture = cache.CaptureAsync("image", "image/png", 1, () => native.Task);
        Task<CachedPreviewImage?> waiting = cache.TryReadAsync("image", TestContext.Current.CancellationToken);
        cache.Clear();
        await capture;
        Assert.Null(await waiting);
        TrackingStream late = new();
        native.SetResult(late);
        for (int attempt = 0; !late.Disposed && attempt < 100; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(late.Disposed);
        Assert.Equal(0, cache.RetainedBytes);
        Assert.Null(await cache.TryReadAsync("image", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 响应缓存_取消单个查看请求_不取消共享捕获()
    {
        using PreviewImageResponseCache cache = new();
        TaskCompletionSource<Stream> native = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task capture = cache.CaptureAsync("image", "image/png", 1, () => native.Task);
        using CancellationTokenSource canceled = new();
        Task<CachedPreviewImage?> first = cache.TryReadAsync("image", canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        native.SetResult(new MemoryStream([9]));
        await capture;
        Assert.Equal(9, (await cache.TryReadAsync("image", TestContext.Current.CancellationToken))!.Bytes[0]);
    }

    [Fact]
    public async Task 响应缓存_原生操作超时但未终止_继续占用准入槽避免无限堆积()
    {
        using PreviewImageResponseCache cache = new(timeout: TimeSpan.FromMilliseconds(30));
        TaskCompletionSource<Stream> one = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Stream> two = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = cache.CaptureAsync("one", "image/png", 1, () => one.Task);
        Task second = cache.CaptureAsync("two", "image/png", 1, () => two.Task);
        await Task.WhenAll(first, second);
        await cache.CaptureAsync("three", "image/png", 1, () => throw new InvalidOperationException("Timed-out native operations still occupy both slots."));
        one.SetResult(new MemoryStream([1])); two.SetResult(new MemoryStream([2]));
        Assert.Equal(0, cache.RetainedBytes);
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
