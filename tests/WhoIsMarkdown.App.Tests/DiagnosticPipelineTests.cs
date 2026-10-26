using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

[Collection("Diagnostic log static isolation")]
public sealed class DiagnosticPipelineTests
{
    [Fact]
    public async Task Settings_TwoReadMergeWriteRounds_RecordInternalStagesWithoutValues()
    {
        await using Fixture fixture = new();
        JsonApplicationSettingsStore store = new(Path.Combine(fixture.Path, "PRIVATE_SETTINGS_FILE.json"));
        ApplicationSettings baseline = store.Load();
        for (int round = 0; round < 2; round++)
        {
            ApplicationSettings changed = baseline with
            {
                BackgroundOpacity = 0.3 + round * 0.1,
                BackgroundImagePath = Path.Combine(fixture.Path, "PRIVATE_SETTINGS_VALUE.png")
            };
            store.SaveChanges(baseline, changed);
            baseline = store.Load();
            Assert.Equal(changed.BackgroundOpacity, baseline.BackgroundOpacity);
        }
        await fixture.Log.StopAsync();
        JsonElement[] rows = fixture.Read();
        foreach (string stage in new[] { "SettingsMerge", "SettingsSerialize", "SettingsBytesWrite", "SettingsDiskFlush",
            "SettingsTemporaryClose", "SettingsAtomicReplace", "SettingsTemporaryCleanup" })
            Assert.Equal(2, rows.Count(row => Has(row, "stage", stage) && Has(row, "outcome", "Succeeded")));
        Assert.Contains(rows, row => Has(row, "stage", "SettingsDeserialize") && Has(row, "outcome", "Succeeded"));
        Assert.Contains(rows, row => Has(row, "stage", "SettingsMutexWait") && Has(row, "execution_kind", "Synchronous"));
        Assert.DoesNotContain("PRIVATE_SETTINGS", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
        Assert.False(CoreDiagnosticBridge.Detailed.Value);
    }

    [Theory]
    [InlineData("missing", 1)]
    [InlineData("json", 2)]
    [InlineData("utf8", 3)]
    [InlineData("null", 4)]
    public async Task Settings_Fallback_RecordsFixedReasonAndKeepsDefaults(string mode, int reason)
    {
        await using Fixture fixture = new();
        string path = Path.Combine(fixture.Path, "settings.json");
        if (mode == "utf8") await File.WriteAllBytesAsync(path, [0xC3, 0x28], TestContext.Current.CancellationToken);
        else if (mode != "missing") await File.WriteAllTextAsync(path, mode == "null" ? "null" : "PRIVATE_INVALID_JSON",
            TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationSettings.DefaultBackgroundOpacity, new JsonApplicationSettingsStore(path).Load().BackgroundOpacity);
        await fixture.Log.StopAsync();
        JsonElement[] rows = fixture.Read();
        Assert.Contains(rows, row => Has(row, "stage", "SettingsFallback") && row.GetProperty("value").GetInt64() == reason);
        if (mode is "json" or "utf8")
            Assert.Contains(rows, row => Has(row, "stage", mode == "json" ? "SettingsDeserialize" : "SettingsDecode")
                && Has(row, "outcome", "Failed"));
        Assert.Contains(rows, row => Has(row, "stage", "SettingsReadTransaction") && Has(row, "outcome", "Succeeded"));
        Assert.DoesNotContain("PRIVATE_INVALID_JSON", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_AtomicReplaceFailure_RecordsFailureAndPreservesOldFile()
    {
        await using Fixture fixture = new();
        string path = Path.Combine(fixture.Path, "settings.json");
        JsonApplicationSettingsStore store = new(path);
        store.Save(new());
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<ApplicationSettingsStoreException>(() => store.Save(new() { BackgroundOpacity = 0.7 }));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(fixture.Path, "*.tmp"));
        await fixture.Log.StopAsync();
        Assert.Contains(fixture.Read(), row => Has(row, "stage", "SettingsAtomicReplace") && Has(row, "outcome", "Failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_MutexContention_ReportsLiveWaitAndTimeoutOrRecovery(bool timeout)
    {
        await using Fixture fixture = new();
        fixture.Log.EnableStallMonitoring();
        JsonApplicationSettingsStore store = new(Path.Combine(fixture.Path, "settings.json"));
        string name = (string)typeof(JsonApplicationSettingsStore).GetField("writerMutexName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        Thread owner = new(() =>
        {
            using Mutex mutex = new(false, name);
            mutex.WaitOne();
            entered.Set();
            release.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true };
        owner.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Task<ApplicationSettings> loading = Task.Run(store.Load, TestContext.Current.CancellationToken);
        try
        {
            await fixture.WaitFor(row => Has(row, "stage", "SlowOperationSample")
                && Has(row.GetProperty("observation"), "PendingStage", "SettingsMutexWait"));
            Assert.False(loading.IsCompleted);
            if (timeout)
            {
                ApplicationSettingsStoreException failure = await Assert.ThrowsAsync<ApplicationSettingsStoreException>(() => loading);
                Assert.IsType<TimeoutException>(failure.InnerException);
            }
            else { release.Set(); await loading; }
        }
        finally
        {
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
            try { await loading; } catch (ApplicationSettingsStoreException) when (timeout) { }
        }
        await fixture.Log.StopAsync();
        Assert.Contains(fixture.Read(), row => Has(row, "stage", "SettingsMutexWait")
            && Has(row, "outcome", timeout ? "Failed" : "Succeeded"));
    }

    [Fact]
    public async Task Settings_AbandonedMutex_RecordsRecoveryAndReleasesLock()
    {
        await using Fixture fixture = new();
        JsonApplicationSettingsStore store = new(Path.Combine(fixture.Path, "settings.json"));
        string name = (string)typeof(JsonApplicationSettingsStore).GetField("writerMutexName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using Mutex mutex = new(false, name);
        Thread owner = new(() => mutex.WaitOne()) { IsBackground = true };
        owner.Start();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        store.Load();
        store.Save(new());
        await fixture.Log.StopAsync();
        Assert.Contains(fixture.Read(), row => Has(row, "stage", "SettingsMutexAbandoned") && row.GetProperty("value").GetInt64() == 1);
    }

    [Fact]
    public async Task RenderQueue_CanceledWhileQueued_RecordsCancellationNotSuccessAndRemainsUsable()
    {
        await using Fixture fixture = new();
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim release = new();
        using MarkdownRenderWorker worker = new(() => { started.Set(); release.Wait(); return new MarkdownRenderer(); });
        Task first = worker.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task second = worker.PrepareAsync(canceled.Token);
        try
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            JsonElement failure = await fixture.WaitFor(row => Has(row, "stage", "RenderQueueWait") && Has(row, "outcome", "Canceled"));
            long operation = failure.GetProperty("operation").GetInt64();
            Assert.DoesNotContain(fixture.Read(), row => Has(row, "stage", "RenderQueueWait")
                && row.GetProperty("operation").GetInt64() == operation && Has(row, "outcome", "Succeeded"));
        }
        finally { release.Set(); await first; }
        await worker.PrepareAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScriptWait_TwoRounds_SeparatesNativePollsAndDelayWithoutPageData()
    {
        await using Fixture fixture = new(detailed: true);
        for (int round = 0; round < 2; round++)
        {
            int polls = 0;
            Task<string> Execute(string expression) => Task.FromResult(expression == "PRIVATE_SCRIPT"
                ? "ignored" : ++polls == 1 ? "null" : "PRIVATE_PAGE_RESULT");
            Assert.Equal("PRIVATE_PAGE_RESULT", await WebViewScriptResult.RunAsync(Execute, "PRIVATE_SCRIPT", "PRIVATE_SLOT",
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        await fixture.Log.StopAsync();
        JsonElement[] rows = fixture.Read();
        JsonElement[] ends = rows.Where(row => Has(row, "stage", "HostScriptWait") && Has(row, "outcome", "Succeeded")).ToArray();
        Assert.Equal(2, ends.Length);
        Assert.All(ends, row =>
        {
            JsonElement summary = row.GetProperty("script_wait");
            Assert.Equal(3, summary.GetProperty("NativeCalls").GetInt32());
            Assert.Equal(2, summary.GetProperty("PollCalls").GetInt32());
            Assert.Equal(1, summary.GetProperty("NullResponses").GetInt32());
            Assert.True(summary.GetProperty("PollDelayMs").GetDouble() > 0);
            Assert.Equal("Ready", summary.GetProperty("Outcome").GetString());
        });
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderWork_FailedOrCanceledAfterAdmission_PreservesOutcome(bool cancel)
    {
        await using Fixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using MarkdownRenderWorker worker = new(() =>
        {
            if (!cancel) throw new InvalidOperationException("PRIVATE_RENDER_FAILURE");
            cancellation.Cancel();
            return new MarkdownRenderer();
        });
        Task work = worker.PrepareAsync(cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => work);
        await fixture.Log.StopAsync();
        Assert.Contains(fixture.Read(), row => Has(row, "stage", "RenderWorkAwait") && Has(row, "outcome", cancel ? "Canceled" : "Failed"));
        Assert.Contains(fixture.Read(), row => Has(row, "stage", "RenderRequest") && Has(row, "outcome", cancel ? "Canceled" : "Failed"));
        Assert.DoesNotContain("PRIVATE_RENDER_FAILURE", JsonSerializer.Serialize(fixture.Read()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptWait_NativeNeverCompletes_RecordsDispatchOrPollTimeout(bool initial)
    {
        await using Fixture fixture = new();
        TaskCompletionSource<string> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<string> Execute(string _) => ++calls == 1 && !initial ? Task.FromResult("ignored") : never.Task;
        Assert.Null(await WebViewScriptResult.RunAsync(Execute, "script", "slot", TimeSpan.FromMilliseconds(80),
            TestContext.Current.CancellationToken));
        Assert.False(never.Task.IsCompleted); // Host timeout is not native cancellation.
        await fixture.Log.StopAsync();
        Assert.Contains(fixture.Read(), row => Has(row, "stage", initial ? "HostScriptDispatch" : "HostScriptPollCall") && Has(row, "outcome", "Failed"));
        JsonElement end = fixture.Read().Single(row => Has(row, "stage", "HostScriptWait") && Has(row, "outcome", "Failed"));
        Assert.Equal("Timeout", end.GetProperty("script_wait").GetProperty("Outcome").GetString());
        never.TrySetResult("late");
    }

    [Theory]
    [InlineData("dispatch")]
    [InlineData("poll")]
    [InlineData("delay")]
    [InlineData("before")]
    public async Task ScriptWait_Cancellation_RecordsCanceledRatherThanAbandoned(string where)
    {
        await using Fixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        TaskCompletionSource<string> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<string> Execute(string _)
        {
            int call = ++calls;
            if ((where == "dispatch" && call == 1) || (where == "poll" && call == 2))
            { cancellation.Cancel(); return never.Task; }
            if (where == "delay" && call == 2) cancellation.Cancel();
            return Task.FromResult(call == 1 ? "ignored" : "null");
        }
        if (where == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebViewScriptResult.RunAsync(Execute, "script", "slot",
            TimeSpan.FromSeconds(2), cancellation.Token));
        await fixture.Log.StopAsync();
        JsonElement[] rows = fixture.Read();
        Assert.Contains(rows, row => Has(row, "stage", "HostScriptWait") && Has(row, "outcome", "Canceled"));
        Assert.DoesNotContain(rows, row => Has(row, "outcome", "Abandoned"));
        never.TrySetResult("late");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptWait_NativeFailure_PreservesExceptionAndRecordsStage(bool poll)
    {
        await using Fixture fixture = new();
        InvalidOperationException expected = new("PRIVATE_URL_OR_CONTENT");
        int calls = 0;
        Task<string> Execute(string _) => ++calls == 1 && poll ? Task.FromResult("ignored") : Task.FromException<string>(expected);
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() => WebViewScriptResult.RunAsync(
            Execute, "script", "slot", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        await fixture.Log.StopAsync();
        JsonElement[] rows = fixture.Read();
        Assert.Contains(rows, row => Has(row, "stage", poll ? "HostScriptPollCall" : "HostScriptDispatch") && Has(row, "outcome", "Failed"));
        Assert.DoesNotContain("PRIVATE_URL_OR_CONTENT", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
        Assert.DoesNotContain(rows, row => Has(row, "outcome", "Abandoned"));
    }

    [Fact]
    public async Task ScriptWait_DirectPoll_EmptyBudgetKeepsBehaviorAndReportsTimeout()
    {
        await using Fixture fixture = new();
        int calls = 0;
        Task<string> Execute(string _) { calls++; return Task.FromResult("ready"); }
        Assert.Null(await WebViewScriptResult.PollAsync(Execute, "slot", TimeSpan.Zero, new CancellationToken(true)));
        Assert.Equal(0, calls);
        Assert.Equal("ready", await WebViewScriptResult.PollAsync(Execute, "slot", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    private static bool Has(JsonElement row, string name, string value)
        => row.TryGetProperty(name, out JsonElement field) && field.GetString() == value;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LocalDiagnosticLog? previous = DiagnosticLog.Current;
        private readonly bool previousDetail = CoreDiagnosticBridge.Detailed.Value;
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WIMD-pipeline-tests-" + Guid.NewGuid().ToString("N"));
        internal LocalDiagnosticLog Log { get; }
        internal Fixture(bool detailed = false)
        {
            Directory.CreateDirectory(Path);
            Log = new(System.IO.Path.Combine(Path, "logs"), "pipeline-test");
            DiagnosticLog.Current = Log;
            CoreDiagnosticBridge.Detailed.Value = detailed;
            CoreDiagnosticBridge.Start();
        }
        internal JsonElement[] Read()
        {
            if (!Directory.Exists(Log.DirectoryPath)) return [];
            List<JsonElement> rows = [];
            foreach (string file in Directory.GetFiles(Log.DirectoryPath, "wimd-*.jsonl"))
            {
                using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(input);
                foreach (string line in reader.ReadToEnd().Split('\n').SkipLast(1))
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    rows.Add(document.RootElement.Clone());
                }
            }
            return [.. rows];
        }
        internal async Task<JsonElement> WaitFor(Func<JsonElement, bool> predicate)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
            {
                foreach (JsonElement row in Read()) if (predicate(row)) return row;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("Expected diagnostic pipeline event was not persisted");
        }
        public async ValueTask DisposeAsync()
        {
            DiagnosticLog.Current = previous;
            CoreDiagnosticBridge.Detailed.Value = previousDetail;
            await Log.StopAsync();
            await Log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Directory.Delete(Path, recursive: true);
        }
    }
}
