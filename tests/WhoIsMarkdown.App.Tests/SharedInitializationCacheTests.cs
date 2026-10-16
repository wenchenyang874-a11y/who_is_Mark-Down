using System.IO;
using WhoIsMarkdown.App.Infrastructure;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class SharedInitializationCacheTests
{
    [Fact]
    public async Task Creation_ConcurrentConsumers_ShareOneTaskAndStartFactoryOnCallerThread()
    {
        SharedInitializationCache<string, object> cache = new();
        TaskCompletionSource<object> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int caller = Environment.CurrentManagedThreadId;
        int starts = 0;
        Task<object> first = cache.GetOrCreate("profile", () =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            starts++;
            return pending.Task;
        });
        Task<object> second = cache.GetOrCreate("profile", () => throw new InvalidOperationException("Must reuse pending creation"));
        Assert.Same(first, second);
        Assert.Equal(1, starts);
        object expected = new();
        pending.SetResult(expected);
        Assert.Same(expected, await first.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Same(expected, await second.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Wait_CancelingOneConsumer_DoesNotCancelSharedCreation()
    {
        SharedInitializationCache<string, object> cache = new();
        TaskCompletionSource<object> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object> shared = cache.GetOrCreate("profile", () => pending.Task);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shared.WaitAsync(canceled.Token));
        Assert.False(shared.IsCompleted);
        object expected = new();
        pending.SetResult(expected);
        Assert.Same(expected, await cache.GetOrCreate("profile", () => throw new InvalidOperationException("Creation canceled globally")));
    }

    [Fact]
    public async Task Failure_SynchronousOrCanceledFactory_IsEvictedForRetry()
    {
        SharedInitializationCache<string, object> cache = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreate("profile", () => throw new InvalidOperationException("test failure")));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreate("profile", () => Task.FromCanceled<object>(canceled.Token)));
        object expected = new();
        Assert.Same(expected, await cache.GetOrCreate("profile", () => Task.FromResult(expected)));
    }

    [Fact]
    public async Task Clear_LateOldCompletion_DoesNotReplaceNewGeneration()
    {
        SharedInitializationCache<string, object> cache = new();
        TaskCompletionSource<object> old = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object> first = cache.GetOrCreate("profile", () => old.Task);
        cache.Clear();
        object expected = new();
        object current = await cache.GetOrCreate("profile", () => Task.FromResult(expected));
        object obsolete = new();
        old.SetResult(obsolete);
        Assert.Same(obsolete, await first);
        cache.Invalidate("profile", obsolete);
        Assert.Same(current, await cache.GetOrCreate("profile", () => throw new InvalidOperationException("Old generation removed new entry")));
        cache.Invalidate("profile", expected);
        Assert.NotSame(current, await cache.GetOrCreate("profile", () => Task.FromResult(new object())));
    }

    [Fact]
    public async Task Keys_DifferentProfiles_DoNotShareValues()
    {
        SharedInitializationCache<string, object> cache = new();
        object first = await cache.GetOrCreate("profile A", () => Task.FromResult(new object()));
        object second = await cache.GetOrCreate("profile B", () => Task.FromResult(new object()));
        Assert.NotSame(first, second);
        Assert.Same(first, await cache.GetOrCreate("profile A", () => throw new InvalidOperationException("No cache hit")));
    }

    [Fact]
    public async Task Environment_RequestFromWorker_IsRejectedBeforeNativeCreation()
    {
        string profile = Path.Combine(Path.GetTempPath(), "WIMD-MtaGuard-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
        {
            _ = WebView2EnvironmentProvider.GetAsync(profile, TestContext.Current.CancellationToken);
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Environment_StaWorkerWithoutDispatcher_IsRejectedBeforeNativeCreation()
    {
        string profile = Path.Combine(Path.GetTempPath(), "WIMD-StaGuard-" + Guid.NewGuid().ToString("N"));
        TaskCompletionSource<Exception?> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                _ = WebView2EnvironmentProvider.GetAsync(profile);
                outcome.SetResult(null);
            }
            catch (Exception exception)
            {
                outcome.SetResult(exception);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Exception? failure = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<InvalidOperationException>(failure);
    }
}
