using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class MarkdownRenderWorkerTests
{
    [Fact]
    public async Task Renderer_IsLazyAndReused()
    {
        int creations = 0;
        using MarkdownRenderWorker worker = new(() =>
        {
            Interlocked.Increment(ref creations);
            return new CallbackRenderer(value => value);
        });
        Assert.Equal(0, creations);
        Assert.Equal("first", await worker.RenderAsync("first", null, null, TestContext.Current.CancellationToken));
        Assert.Equal("second", await worker.RenderAsync("second", null, null, TestContext.Current.CancellationToken));
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task CanceledRequest_DoesNotConstructRenderer()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MarkdownRenderWorker worker = new(() => throw new InvalidOperationException("Must remain lazy"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            worker.RenderAsync("ignored", null, null, cancellation.Token));
    }

    [Fact]
    public async Task CanceledQueue_DoesNotParseObsoleteSnapshots()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> parsed = [];
        using MarkdownRenderWorker worker = new(() => new CallbackRenderer(value =>
        {
            parsed.Add(value);
            if (value == "active")
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }

            return value;
        }));
        Task<string> active = worker.RenderAsync("active", null, null, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using CancellationTokenSource obsolete = new();
            Task<MarkdownRenderResult> queued = worker.RenderSnapshotAsync(
                () => throw new InvalidOperationException("Canceled queue must not materialize snapshot"), null, null, obsolete.Token);
            Task<string> latest = worker.RenderAsync("latest", null, null, TestContext.Current.CancellationToken);
            obsolete.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(latest.IsCompleted);
            release.Set();
            Assert.Equal("active", await active);
            Assert.Equal("latest", await latest);
            Assert.Equal(["active", "latest"], parsed);
        }
        finally
        {
            release.Set();
            await active;
        }
    }

    [Fact]
    public async Task Dispose_DuringParseAllowsUnwindAndRejectsNewRequests()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using MarkdownRenderWorker worker = new(() => new CallbackRenderer(value =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return value;
        }));
        Task<string> active = worker.RenderAsync("active", null, null, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using CancellationTokenSource canceled = new();
            Task<string> queued = worker.RenderAsync("queued", null, null, canceled.Token);
            worker.Dispose();
            worker.Dispose();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                worker.RenderAsync("rejected", null, null, TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            Assert.Equal("active", await active);
        }
    }

    [Fact]
    public async Task Snapshot_FailedMaterialization_ReleasesGateWithoutCreatingRenderer()
    {
        int creations = 0;
        using MarkdownRenderWorker worker = new(() =>
        {
            creations++;
            return new CallbackRenderer(value => value);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RenderSnapshotAsync(
            () => throw new InvalidOperationException("Expected snapshot failure"), null, null, TestContext.Current.CancellationToken));
        Assert.Equal(0, creations);
        Assert.Equal("latest", (await worker.RenderSnapshotAsync(() => "latest", null, null, TestContext.Current.CancellationToken)).BodyHtml);
        Assert.Equal(1, creations);
    }

    [Fact]
    public async Task Snapshot_CanceledWhileMaterializing_DoesNotParse()
    {
        using CancellationTokenSource cancellation = new();
        using MarkdownRenderWorker worker = new(() => throw new InvalidOperationException("Canceled snapshot must not create renderer"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RenderSnapshotAsync(() =>
        {
            cancellation.Cancel();
            return "obsolete";
        }, null, null, cancellation.Token));
    }

    [Fact]
    public async Task FailedParse_ReleasesGateForNextRequest()
    {
        using MarkdownRenderWorker worker = new(() => new CallbackRenderer(value =>
            value == "fail" ? throw new InvalidOperationException("Expected test failure") : value));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RenderAsync("fail", null, null, TestContext.Current.CancellationToken));
        Assert.Equal("recovered", await worker.RenderAsync("recovered", null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Prepare_ConstructsOnceWithoutParsingAndRenderReusesInstance()
    {
        int creations = 0;
        int parses = 0;
        using MarkdownRenderWorker worker = new(() =>
        {
            Interlocked.Increment(ref creations);
            return new CallbackRenderer(value => { Interlocked.Increment(ref parses); return value; });
        });
        await Task.WhenAll(worker.PrepareAsync(TestContext.Current.CancellationToken),
            worker.PrepareAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, creations);
        Assert.Equal(0, parses);
        Assert.Equal("ready", await worker.RenderAsync("ready", null, null, TestContext.Current.CancellationToken));
        Assert.Equal(1, creations);
        Assert.Equal(1, parses);
    }

    [Fact]
    public async Task Prepare_CanceledBeforeAdmission_DoesNotConstructRenderer()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MarkdownRenderWorker worker = new(() => throw new InvalidOperationException("Must remain lazy"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.PrepareAsync(cancellation.Token));
    }

    [Fact]
    public async Task Prepare_InProgress_SerializesRenderAndCloseRejectsNewRequests()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int creations = 0;
        using MarkdownRenderWorker worker = new(() =>
        {
            Interlocked.Increment(ref creations);
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new CallbackRenderer(value => value);
        });
        Task preparation = worker.PrepareAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Task<string> render = worker.RenderAsync("queued", null, null, TestContext.Current.CancellationToken);
            Assert.False(render.IsCompleted);
            worker.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.PrepareAsync(TestContext.Current.CancellationToken));
            release.Set();
            await preparation;
            Assert.Equal("queued", await render);
            Assert.Equal(1, creations);
        }
        finally
        {
            release.Set();
            await preparation;
        }
    }

    [Fact]
    public async Task Prepare_FailedFactory_ReportsFailureAndDisposesNormally()
    {
        using MarkdownRenderWorker worker = new(() => throw new InvalidOperationException("Expected preparation failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.PrepareAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RenderAsync("unused", null, null, TestContext.Current.CancellationToken));
    }

    private sealed class CallbackRenderer(Func<string, string> callback) : IMarkdownRenderer
    {
        public string RenderBody(string markdown, string? documentPath = null, RemoteImagePolicy? remoteImagePolicy = null)
            => callback(markdown);
    }
}
