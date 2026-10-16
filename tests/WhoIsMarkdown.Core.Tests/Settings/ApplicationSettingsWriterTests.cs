using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ApplicationSettingsWriterTests
{
    [Fact]
    public async Task Enqueue_BurstAndFlush_CoalescesSnapshotsWithoutCallingStoreOnCaller()
    {
        RecordingStore store = new();
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.FromSeconds(30));
        for (int index = 1; index <= 100; index++) writer.Enqueue(new() { BackgroundOpacity = index / 100.0 });
        Assert.Equal(0, store.Writes);
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, store.Writes);
        Assert.Equal(1, store.Load().BackgroundOpacity);
        Assert.False(writer.NeedsFlush);
        writer.Stop();
    }

    [Fact]
    public async Task Enqueue_DuringBlockedWrite_ReturnToOriginalValueIsNotLost()
    {
        RecordingStore store = new() { Block = true };
        ApplicationSettings initial = new();
        ApplicationSettingsWriter writer = new(store, initial, TimeSpan.Zero);
        writer.Enqueue(initial with { BackgroundOpacity = 0.9 });
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        writer.Enqueue(initial);
        Task<SettingsWriteResult> flushing = writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.False(flushing.IsCompleted);
        store.Release.Set();
        Assert.True((await flushing).Succeeded);
        Assert.Equal(initial.BackgroundOpacity, store.Load().BackgroundOpacity);
        Assert.Equal(2, store.Writes);
        writer.Stop();
    }

    [Fact]
    public async Task Flush_FailedWrite_RetainsBaselineAndRetriesLatestState()
    {
        RecordingStore store = new() { Fail = true };
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.FromSeconds(30));
        writer.Enqueue(new() { BackgroundOpacity = 0.7, IsRecentPaneExpanded = false });
        SettingsWriteResult failure = await writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.False(failure.Succeeded);
        Assert.DoesNotContain("secret", failure.ErrorMessage);
        Assert.True(writer.NeedsFlush);
        Assert.True(store.Load().IsRecentPaneExpanded);
        store.Fail = false;
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(0.7, store.Load().BackgroundOpacity);
        Assert.False(store.Load().IsRecentPaneExpanded);
        writer.Stop();
    }

    [Fact]
    public async Task Flush_CanceledWait_DoesNotCancelActiveWrite()
    {
        RecordingStore store = new() { Block = true };
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.Zero);
        writer.Enqueue(new() { IsRecentPaneExpanded = false });
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.FlushAsync(canceled.Token));
        store.Release.Set();
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.False(store.Load().IsRecentPaneExpanded);
        writer.Stop();
    }

    [Fact]
    public async Task Snapshot_MutableInputCollections_AreDetachedBeforeWorkerUse()
    {
        List<RecentFileEntry> recent = [new(Path.GetFullPath("captured.md"), DateTimeOffset.UtcNow)];
        Dictionary<string, ShortcutGesture> shortcuts = new() { ["bold"] = new() { Key = "Q", Control = true } };
        List<string> rules = ["domain:example.com"];
        RecordingStore store = new();
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.FromSeconds(30));
        writer.Enqueue(new()
        {
            RecentFiles = recent,
            ShortcutOverrides = shortcuts,
            ImageInsertion = new() { TrustMode = RemoteImageTrustMode.AllowList, RemoteImageRules = rules }
        });
        recent.Clear(); shortcuts.Clear(); rules.Clear();
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Single(store.Load().RecentFiles);
        Assert.Single(store.Load().ShortcutOverrides);
        Assert.Single(store.Load().ImageInsertion.RemoteImageRules);
        writer.Stop();
    }

    [Fact]
    public async Task Stop_PendingDebounce_DropsWorkAndRejectsFurtherRequests()
    {
        RecordingStore store = new();
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.FromSeconds(30));
        writer.Enqueue(new());
        writer.Stop();
        Assert.Throws<ObjectDisposedException>(() => writer.Enqueue(new()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Observer_Throws_DoesNotStrandFlushOrLaterRequests()
    {
        RecordingStore store = new();
        ApplicationSettingsWriter writer = new(store, new(), TimeSpan.Zero);
        writer.WriteCompleted += _ => throw new InvalidOperationException("observer disappeared");
        writer.Enqueue(new() { BackgroundOpacity = 0.9 });
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        writer.Enqueue(new() { BackgroundOpacity = 0.2 });
        Assert.True((await writer.FlushAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(0.2, store.Load().BackgroundOpacity);
        writer.Stop();
    }

    private sealed class RecordingStore : IApplicationSettingsStore
    {
        private ApplicationSettings persisted = new();
        public bool Block { get; init; }
        public bool Fail { get; set; }
        public int Writes { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public ApplicationSettings Load() => persisted;
        public void Save(ApplicationSettings settings) => persisted = settings;
        public void SaveChanges(ApplicationSettings baseline, ApplicationSettings settings)
        {
            Writes++;
            Started.TrySetResult();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("blocked test write");
            if (Fail) throw new IOException("secret must not be reported");
            persisted = ApplicationSettingsChanges.Merge(persisted, baseline, settings);
        }
    }
}
