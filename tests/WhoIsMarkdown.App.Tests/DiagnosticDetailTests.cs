using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Diagnostics;
using WhoIsMarkdown.Core.Documents;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

[Collection("Diagnostic log static isolation")]
public sealed class DiagnosticDetailTests
{
    [Fact]
    public async Task PendingBlockedOperation_BeforeEnd_PersistsOwnerWaitCpuAndCheckpoint()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "isolated-detail-test");
        log.EnableStallMonitoring();
        using ManualResetEventSlim release = new();
        Thread target = new(() =>
        {
            using DiagnosticOperation operation = log.Begin(DiagnosticStage.WindowXaml, 17);
            log.SetXamlCheckpoint(XamlConstructionCheckpoint.EditorContainer);
            release.Wait();
            operation.Succeed();
        })
        { IsBackground = true };
        target.Start();
        try
        {
            JsonElement sample = await WaitForSample(directory);
            JsonElement observation = sample.GetProperty("observation");
            Assert.Equal("WindowXaml", observation.GetProperty("PendingStage").GetString());
            Assert.Equal("Wait", observation.GetProperty("ThreadState").GetString());
            Assert.False(string.IsNullOrWhiteSpace(observation.GetProperty("WaitReason").GetString()));
            Assert.Equal("EditorContainer", observation.GetProperty("XamlCheckpoint").GetString());
            Assert.True(observation.GetProperty("OwnerCpuMs").GetDouble() < sample.GetProperty("duration_ms").GetDouble());
            Assert.True(observation.GetProperty("OwnerNativeThreadId").GetInt32() > 0);
            Assert.True(observation.GetProperty("ProcessReadBytes").GetUInt64() >= 0);
            Assert.True(observation.GetProperty("PoolThreads").GetInt32() >= 0);
            Assert.Contains(nameof(PendingBlockedOperation_BeforeEnd_PersistsOwnerWaitCpuAndCheckpoint),
                observation.GetProperty("OriginFrames").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(directory.Read(), row => Has(row, "outcome", "Succeeded"));
        }
        finally
        {
            release.Set();
            Assert.True(target.Join(TimeSpan.FromSeconds(5)));
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Contains(directory.Read(), row => Has(row, "outcome", "Succeeded") && row.GetProperty("owner_cpu_ms").GetDouble() >= 0);
    }

    [Fact]
    public async Task PendingCpuOperation_RecordsCpuInsteadOfTreatingWallTimeAsWait()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "isolated-cpu-test");
        log.EnableStallMonitoring();
        using ManualResetEventSlim release = new();
        Thread target = new(() =>
        {
            using DiagnosticOperation operation = log.Begin(DiagnosticStage.WindowXaml);
            while (!release.IsSet) Thread.SpinWait(10000);
            operation.Succeed();
        })
        { IsBackground = true };
        target.Start();
        try
        {
            JsonElement sample = await WaitForSample(directory);
            Assert.True(sample.GetProperty("observation").GetProperty("OwnerCpuMs").GetDouble() > 20);
        }
        finally
        {
            release.Set();
            Assert.True(target.Join(TimeSpan.FromSeconds(5)));
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Completion_OnDifferentThread_DoesNotSubtractUnrelatedCpuCounters()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "isolated-thread-test");
        DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentRead);
        operation.Succeed();
        Thread completion = new(operation.Dispose);
        completion.Start();
        Assert.True(completion.Join(TimeSpan.FromSeconds(5)));
        await log.StopAsync();
        JsonElement end = directory.Read().Single(row => Has(row, "outcome", "Succeeded"));
        Assert.NotEqual(end.GetProperty("native_thread_id").GetInt32(), end.GetProperty("owner_native_thread_id").GetInt32());
        Assert.False(end.TryGetProperty("owner_cpu_ms", out _));
    }

    [Fact]
    public async Task FailedSink_WithObserver_StopsBackgroundThreadAndLateWrites()
    {
        using DetailDirectory directory = new();
        string blocked = System.IO.Path.Combine(directory.Path, "blocked");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(blocked, "keep", TestContext.Current.CancellationToken);
        LocalDiagnosticLog log = new(blocked, "isolated-failure-test");
        log.EnableStallMonitoring();
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentRead)) operation.Succeed();
        await log.StopAsync();
        await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(log.FailureType);
        log.EnableStallMonitoring();
        await log.StopAsync();
        Assert.Equal("keep", await File.ReadAllTextAsync(blocked, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("ICSharpCode.AvalonEdit", DiagnosticAssembly.AvalonEdit)]
    [InlineData("PresentationFramework", DiagnosticAssembly.PresentationFramework)]
    [InlineData("Microsoft.Web.WebView2.Wpf", DiagnosticAssembly.WebViewWpf)]
    public void AssemblyIdentity_KnownAssembly_UsesFixedToken(string name, object expected)
        => Assert.Equal((DiagnosticAssembly)expected, DiagnosticAssemblyObserver.Identify(name));

    [Theory]
    [InlineData("private-file_SECRET_API_KEY")]
    [InlineData("C:\\private\\document.md")]
    [InlineData(null)]
    public void AssemblyIdentity_ArbitraryName_IsNotLogged(string? name)
        => Assert.Null(DiagnosticAssemblyObserver.Identify(name));

    [Fact]
    public void StartupRecorder_LaunchPlan_DoesNotElevateEditorOrUseShellArgumentConcatenation()
    {
        ProcessStartInfo start = StartupDiagnosticLauncher.CreateStartInfo(@"C:\a path with space\中文");
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal("", start.Verb);
        Assert.Contains(@"C:\a path with space\中文\Diagnostics\Capture-Startup.ps1", start.ArgumentList);
        Assert.DoesNotContain("runas", start.ArgumentList);
    }

    [Fact]
    public void StartupRecorder_CurrentDocument_IsAnIndependentNonElevatedArgument()
    {
        const string documentPath = @"C:\notes with spaces\中文.md";
        ProcessStartInfo start = StartupDiagnosticLauncher.CreateStartInfo(@"C:\WIMD", documentPath);
        Assert.False(start.UseShellExecute);
        Assert.Equal("-DocumentPath", start.ArgumentList[^2]);
        Assert.Equal(documentPath, start.ArgumentList[^1]);
        Assert.Empty(start.Arguments);
    }

    private static bool Has(JsonElement row, string property, string value)
        => row.TryGetProperty(property, out JsonElement field) && field.GetString() == value;

    [Fact]
    public async Task AsyncTaskCompleted_BeforeCallerResumes_PersistsCompletionAndResumeGap()
    {
        using DetailDirectory directory = new();
        await using LocalDiagnosticLog log = new(directory.Path, "async-timeline-test");
        log.EnableStallMonitoring();
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim returned = new();
        QueuedSynchronizationContext context = new();
        Task<int>? measured = null;
        Thread initiator = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            measured = DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead, () => source.Task, window: 61);
            returned.Set();
        })
        { IsBackground = true };
        initiator.Start();
        try
        {
            Assert.True(returned.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            JsonElement waiting = await WaitForStage(directory, "SlowOperationSample", row =>
                row.TryGetProperty("async_timeline", out JsonElement state) && Has(state, "Phase", "AwaitingTask"));
            Assert.Equal("AsyncAwait", waiting.GetProperty("execution_kind").GetString());
            source.SetResult(37);
            JsonElement completed = await WaitForStage(directory, "AsyncTaskCompletionObserved");
            Assert.False(measured!.IsCompleted);
            Assert.Equal("TaskCompletedAwaitingContinuation", completed.GetProperty("async_timeline").GetProperty("Phase").GetString());
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.True(context.RunOne());
            Assert.Equal(37, await measured);
            await log.StopAsync();
            JsonElement end = directory.Read().Single(row => Has(row, "stage", "DocumentRead") && Has(row, "outcome", "Succeeded"));
            JsonElement timeline = end.GetProperty("async_timeline");
            Assert.Equal("Resumed", timeline.GetProperty("Phase").GetString());
            Assert.Equal("ObserverPollUpperBound", timeline.GetProperty("CompletionObservation").GetString());
            Assert.True(timeline.GetProperty("ObservationToResumeMs").GetDouble() >= 50);
            Assert.False(end.TryGetProperty("owner_cpu_ms", out _));
            Assert.Equal(completed.GetProperty("operation").GetInt64(), end.GetProperty("operation").GetInt64());
            Assert.Equal(61, end.GetProperty("window").GetInt32());
        }
        finally
        {
            source.TrySetResult(37);
            context.RunOne();
            Assert.True(initiator.Join(TimeSpan.FromSeconds(5)));
            DiagnosticLog.Current = previous;
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AsyncDelegate_BlockedBeforeTaskReturn_IdentifiesSynchronousDispatchThread()
    {
        using DetailDirectory directory = new();
        await using LocalDiagnosticLog log = new(directory.Path, "async-dispatch-test");
        log.EnableStallMonitoring();
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        using ManualResetEventSlim release = new();
        Task? measured = null;
        Thread worker = new(() => measured = DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead, () =>
        {
            release.Wait();
            return Task.CompletedTask;
        }))
        { IsBackground = true };
        worker.Start();
        try
        {
            JsonElement sample = await WaitForStage(directory, "SlowOperationSample", row =>
                Has(row.GetProperty("observation"), "PendingStage", "AsyncActionDispatch"));
            Assert.Equal("Synchronous", sample.GetProperty("execution_kind").GetString());
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            if (measured is not null) await measured;
            DiagnosticLog.Current = previous;
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        JsonElement end = directory.Read().Single(row => Has(row, "stage", "DocumentRead") && Has(row, "outcome", "Succeeded"));
        Assert.True(end.GetProperty("async_timeline").GetProperty("DispatchMs").GetDouble() >= 500);
        Assert.Equal("CompletedAtReturn", end.GetProperty("async_timeline").GetProperty("CompletionObservation").GetString());
    }

    [Fact]
    public void AwaitTimeline_CompletionWithoutPoll_DoesNotInventCompletionTimestamp()
    {
        TaskCompletionSource source = new();
        DiagnosticAwaitState state = new(source.Task, 12);
        source.SetResult();
        state.Resume(source.Task);
        Assert.False(state.ObserveCompletion());
        DiagnosticAwaitSnapshot snapshot = state.Snapshot();
        Assert.Equal("Resumed", snapshot.Phase);
        Assert.Equal("RanToCompletion", snapshot.LatestTaskStatus);
        Assert.Null(snapshot.CompletionObservedUtc);
        Assert.Null(snapshot.ObservationToResumeMs);
        Assert.Equal("NotObservedBeforeResume", snapshot.CompletionObservation);
    }

    [Fact]
    public void AwaitTimeline_CompletedTask_UsesReturnCheckpointAndOnlyObservesOnce()
    {
        TaskCompletionSource source = new();
        DiagnosticAwaitState state = new(source.Task, 2);
        Assert.False(state.ObserveCompletion());
        source.SetResult();
        Assert.True(state.ObserveCompletion());
        Assert.False(state.ObserveCompletion());
        state.Resume(source.Task);
        DiagnosticAwaitSnapshot snapshot = state.Snapshot();
        Assert.Equal("ObserverPollUpperBound", snapshot.CompletionObservation);
        Assert.NotNull(snapshot.ObservationToResumeMs);
        DiagnosticAwaitState immediate = new(Task.CompletedTask, 3);
        Assert.Equal("CompletedAtReturn", immediate.Snapshot().CompletionObservation);
    }

    [Fact]
    public void AwaitTimeline_FaultAndCancellation_RecordsOnlyTaskStatus()
    {
        DiagnosticAwaitState canceled = new(Task.FromCanceled(new CancellationToken(true)), 0);
        DiagnosticAwaitState faulted = new(Task.FromException(new IOException("SECRET_PRIVATE_PATH")), 0);
        Assert.Equal("Canceled", canceled.Snapshot().LatestTaskStatus);
        Assert.Equal("Faulted", faulted.Snapshot().LatestTaskStatus);
        Assert.DoesNotContain("SECRET_PRIVATE_PATH", JsonSerializer.Serialize(faulted.Snapshot()), StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitTimeline_AbandonedTask_DoesNotKeepResultOrTaskAlive()
    {
        DiagnosticAwaitState state = CreateWeakAwaitState();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(state.ObserveCompletion());
        Assert.Equal("TaskNotReachable", state.Snapshot().Phase);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DiagnosticAwaitState CreateWeakAwaitState() => new(new TaskCompletionSource<object>().Task, 0);

    [Fact]
    public async Task AsyncWrappers_FastQuietCalls_DoNotCreateLogStorm()
    {
        using DetailDirectory directory = new();
        await using LocalDiagnosticLog log = new(directory.Path, "quiet-await-test");
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        try
        {
            await DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead, () => Task.CompletedTask, onlySlow: true);
            Assert.Equal(9, await DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead, () => Task.FromResult(9), onlySlow: true));
        }
        finally { DiagnosticLog.Current = previous; await log.StopAsync(); }
        Assert.Empty(directory.Read());
    }

    [Fact]
    public async Task AsyncWrappers_NonGenericCall_EnablesNestedCoreDetailAndRestoresContext()
    {
        using DetailDirectory directory = new();
        await using LocalDiagnosticLog log = new(directory.Path, "non-generic-await-test");
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        bool previousDetail = CoreDiagnosticBridge.Detailed.Value;
        try
        {
            CoreDiagnosticBridge.Detailed.Value = false;
            await DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead, () =>
            {
                Assert.True(CoreDiagnosticBridge.Detailed.Value);
                return Task.CompletedTask;
            });
            Assert.False(CoreDiagnosticBridge.Detailed.Value);
            Assert.Equal(0, log.Context.Value.Operation);
            await Assert.ThrowsAsync<IOException>(() => DiagnosticLog.MeasureAsync(DiagnosticStage.DocumentRead,
                () => throw new IOException("SECRET_PRIVATE_PATH")));
            Assert.False(CoreDiagnosticBridge.Detailed.Value);
        }
        finally
        {
            CoreDiagnosticBridge.Detailed.Value = previousDetail;
            DiagnosticLog.Current = previous;
            await log.StopAsync();
        }
        JsonElement[] rows = directory.Read();
        Assert.Contains(rows, row => Has(row, "stage", "AsyncActionDispatch") && Has(row, "outcome", "Failed"));
        Assert.DoesNotContain("SECRET_PRIVATE_PATH", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue((callback, state));
        internal bool RunOne()
        {
            if (!queue.TryDequeue(out var item)) return false;
            item.Callback(item.State);
            return true;
        }
    }

    [Fact]
    public async Task CoreAsyncWait_BeforeCompletion_PersistsTaskStateWithoutAppWrapper()
    {
        using DetailDirectory directory = new();
        await using LocalDiagnosticLog log = new(directory.Path, "core-await-test");
        log.EnableStallMonitoring();
        CoreDiagnosticBridge.Start();
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> measured = CoreOperationTrace.MeasureAsync("DocumentBodyWrite", () => source.Task);
        try
        {
            JsonElement sample = await WaitForStage(directory, "SlowOperationSample", row =>
                row.TryGetProperty("async_timeline", out JsonElement state) && Has(state, "Phase", "AwaitingTask"));
            Assert.Equal("DocumentBodyWrite", sample.GetProperty("observation").GetProperty("PendingStage").GetString());
            Assert.False(measured.IsCompleted);
            source.SetResult(27);
            Assert.Equal(27, await measured);
        }
        finally
        {
            source.TrySetResult(27);
            await measured;
            DiagnosticLog.Current = previous;
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        JsonElement end = directory.Read().Single(row => Has(row, "stage", "DocumentBodyWrite") && Has(row, "outcome", "Succeeded"));
        Assert.Equal("Resumed", end.GetProperty("async_timeline").GetProperty("Phase").GetString());
        Assert.Equal("RanToCompletion", end.GetProperty("async_timeline").GetProperty("LatestTaskStatus").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocumentSave_TwoRounds_WritesDetailedPrivateSafeJsonWithoutAppWrapper(bool bom)
    {
        using DetailDirectory directory = new();
        string documentPath = Path.Combine(directory.Path, "PRIVATE_FILENAME.md");
        Directory.CreateDirectory(directory.Path);
        string logPath = Path.Combine(directory.Path, "logs");
        await using LocalDiagnosticLog log = new(logPath, "save-detail-test");
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        CoreDiagnosticBridge.Start();
        const string text = "PRIVATE_BODY_SECRET\r\n中文";
        try
        {
            DocumentFileService service = new();
            for (int round = 0; round < 2; round++)
            {
                await service.WriteAsync(new(documentPath, text, bom), TestContext.Current.CancellationToken);
                Assert.Equal(text, (await service.ReadAsync(documentPath, TestContext.Current.CancellationToken)).Text);
            }
        }
        finally { DiagnosticLog.Current = previous; await log.StopAsync(); }
        JsonElement[] rows = ReadJsonFiles(logPath);
        foreach (string stage in new[] { "DocumentWriteDirectoryCheck", "DocumentTemporaryOpen", "DocumentEncode",
            "DocumentBodyWrite", "DocumentBufferFlush", "DocumentDiskFlush", "DocumentTemporaryClose", "DocumentAtomicReplace",
            "DocumentTemporaryCleanup" })
            Assert.Equal(2, rows.Count(row => Has(row, "stage", stage) && Has(row, "outcome", "Succeeded")));
        Assert.Equal(bom ? 2 : 0, rows.Count(row => Has(row, "stage", "DocumentBomWrite") && Has(row, "outcome", "Succeeded")));
        JsonElement[] saves = rows.Where(row => Has(row, "stage", "DocumentFileWrite") && Has(row, "outcome", "Succeeded")).ToArray();
        Assert.Equal(2, saves.Length);
        Assert.All(saves, row => Assert.Equal("Resumed", row.GetProperty("async_timeline").GetProperty("Phase").GetString()));
        long[] saveIds = saves.Select(row => row.GetProperty("operation").GetInt64()).ToArray();
        Assert.All(rows.Where(row => Has(row, "outcome", "Started") && (Has(row, "stage", "DocumentDiskFlush")
                || Has(row, "stage", "DocumentBodyWrite") || Has(row, "stage", "DocumentTemporaryClose"))),
            row => Assert.Contains(row.GetProperty("parent_operation").GetInt64(), saveIds));
        Assert.All(rows.Where(row => Has(row, "stage", "DocumentEncodedBytes")), row =>
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), row.GetProperty("value").GetInt64()));
        string serialized = JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("PRIVATE_BODY_SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_FILENAME", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("encode", "DocumentEncode", "Failed")]
    [InlineData("replace", "DocumentAtomicReplace", "Failed")]
    [InlineData("cancel", "DocumentBodyWrite", "Canceled")]
    [InlineData("directory", "DocumentWriteDirectoryCheck", "Failed")]
    public async Task DocumentSave_FailureOrCancellation_PersistsExactStageAndKeepsOriginal(string mode, string stage, string outcome)
    {
        using DetailDirectory directory = new();
        Directory.CreateDirectory(directory.Path);
        string path = Path.Combine(directory.Path, "PRIVATE_TARGET.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        string logPath = Path.Combine(directory.Path, "logs");
        await using LocalDiagnosticLog log = new(logPath, "save-failure-test");
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        CoreDiagnosticBridge.Start();
        FileStream? locked = mode == "replace" ? new(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        try
        {
            string target = mode == "directory" ? Path.Combine(directory.Path, "missing", "note.md") : path;
            string text = mode == "encode" ? "\uD800" : "PRIVATE_NEW_BODY";
            CancellationToken token = mode == "cancel" ? new(true) : TestContext.Current.CancellationToken;
            Task saving = new DocumentFileService().WriteAsync(new(target, text, false), token);
            if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
            else await Assert.ThrowsAsync<DocumentFileException>(() => saving);
        }
        finally { locked?.Dispose(); DiagnosticLog.Current = previous; await log.StopAsync(); }
        JsonElement[] rows = ReadJsonFiles(logPath);
        Assert.Contains(rows, row => Has(row, "stage", stage) && Has(row, "outcome", outcome));
        Assert.Contains(rows, row => Has(row, "stage", "DocumentFileWrite") && Has(row, "outcome", outcome));
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
        Assert.DoesNotContain("PRIVATE_TARGET", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_NEW_BODY", JsonSerializer.Serialize(rows), StringComparison.Ordinal);
    }

    private static JsonElement[] ReadJsonFiles(string path) => Directory.GetFiles(path, "wimd-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    [Fact]
    public async Task ObserverCapacity_ConcurrentRegistrations_ReportsExactRejectedCountBeforeStop()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "capacity-test");
        using DiagnosticStallMonitor monitor = new(log);
        int accepted = 0;
        Thread[] producers = Enumerable.Range(0, 4).Select(producer => new Thread(() =>
        {
            for (int index = 0; index < 40; index++)
                if (monitor.Enter(producer * 40 + index + 1, DiagnosticStage.DocumentDecode, 7, 0,
                    DiagnosticThreadProbe.NativeThreadId, null, "", Stopwatch.GetTimestamp(), DiagnosticExecutionKind.Unspecified))
                    Interlocked.Increment(ref accepted);
        })).ToArray();
        try
        {
            foreach (Thread producer in producers) producer.Start();
            foreach (Thread producer in producers) Assert.True(producer.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(128, accepted);
            JsonElement coverage = await WaitForStage(directory, "DiagnosticCoverage", row =>
                Has(row.GetProperty("coverage"), "Reason", "ObservationCapacity")
                    && row.GetProperty("coverage").GetProperty("AttemptsTotal").GetInt64() == 32);
            Assert.Equal(7, coverage.GetProperty("coverage").GetProperty("LastWindow").GetInt32());
        }
        finally
        {
            monitor.Dispose();
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await log.StopAsync();
        }
    }

    [Fact]
    public async Task ConcurrentFirstRegistrations_WithoutDispatcher_WakeSleepingObserver()
    {
        for (int round = 0; round < 3; round++)
        {
            using DetailDirectory directory = new();
            LocalDiagnosticLog log = new(directory.Path, "concurrent-wake-test");
            using DiagnosticStallMonitor monitor = new(log);
            using Barrier start = new(3);
            using ManualResetEventSlim release = new();
            Thread[] targets = Enumerable.Range(1, 2).Select(id => new Thread(() =>
            {
                start.SignalAndWait();
                monitor.Enter(id, DiagnosticStage.DocumentDecode, id, 0, DiagnosticThreadProbe.NativeThreadId,
                    null, "", Stopwatch.GetTimestamp(), DiagnosticExecutionKind.Unspecified);
                release.Wait();
                monitor.Leave(id);
            })
            { IsBackground = true }).ToArray();
            try
            {
                foreach (Thread target in targets) target.Start();
                start.SignalAndWait(TestContext.Current.CancellationToken);
                await WaitForStage(directory, "SlowOperationSample", _ => directory.Read()
                    .Where(row => Has(row, "stage", "SlowOperationSample"))
                    .Select(row => row.GetProperty("operation").GetInt64()).Distinct().Count() == 2);
            }
            finally
            {
                release.Set();
                foreach (Thread target in targets) Assert.True(target.Join(TimeSpan.FromSeconds(5)));
                monitor.Dispose();
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                await log.StopAsync();
            }
        }
    }

    [Fact]
    public async Task ObserverRegistration_DuplicateId_ReportsReasonWithoutReplacingOriginal()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "duplicate-registration-test");
        using DiagnosticStallMonitor monitor = new(log);
        try
        {
            Assert.True(monitor.Enter(1, DiagnosticStage.DocumentDecode, 1, 0, DiagnosticThreadProbe.NativeThreadId,
                null, "", Stopwatch.GetTimestamp(), DiagnosticExecutionKind.Unspecified));
            Assert.False(monitor.Enter(1, DiagnosticStage.DocumentRead, 2, 0, DiagnosticThreadProbe.NativeThreadId,
                null, "", Stopwatch.GetTimestamp(), DiagnosticExecutionKind.Unspecified));
            JsonElement coverage = await WaitForStage(directory, "DiagnosticCoverage",
                row => Has(row.GetProperty("coverage"), "Reason", "ObservationDuplicate"));
            Assert.Equal(2, coverage.GetProperty("coverage").GetProperty("LastWindow").GetInt32());
        }
        finally
        {
            monitor.Dispose();
            await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await log.StopAsync();
        }
    }

    [Fact]
    public void CoverageTracker_RepeatedReason_AggregatesWithForcedFinalFlush()
    {
        DiagnosticCoverageTracker coverage = new();
        for (int index = 0; index < 1000; index++)
            coverage.Add(DiagnosticCoverageReason.CaptureCooldown, DiagnosticStage.RenderExecution, 3, index, 11, 4, 2, 1200);
        DiagnosticCoverageSnapshot first = Assert.Single(coverage.TakeDue(false));
        Assert.Equal(1000, first.AttemptsTotal);
        Assert.Equal(2000, first.TargetAttemptsTotal);
        Assert.False(coverage.HasPending);
        for (int index = 0; index < 1000; index++)
            coverage.Add(DiagnosticCoverageReason.CaptureCooldown, DiagnosticStage.RenderExecution, 3, index, 11, 4, 2, 1100);
        Assert.Empty(coverage.TakeDue(false));
        DiagnosticCoverageSnapshot final = Assert.Single(coverage.TakeDue(true));
        Assert.Equal(2000, final.AttemptsTotal);
        Assert.Equal(1000, final.AttemptsSinceLastReport);
        Assert.Equal(999, final.LastOperation);
        Assert.Equal(1100, final.LastRemainingCooldownMs);
        Assert.False(coverage.HasPending);
    }

    [Fact]
    public async Task Capture_AlreadyRunningAndCoolingDown_PersistsBothReasons()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "capture-skipped-test");
        using DiagnosticStackCapture capture = new(log);
        DiagnosticStackTarget[] targets = [new(DiagnosticStage.RenderExecution, 4, 71, 70, int.MaxValue)];
        try
        {
            capture.TryCapture(targets);
            capture.TryCapture(targets);
            await WaitForStage(directory, "DiagnosticCoverage", row => Has(row.GetProperty("coverage"), "Reason", "CaptureBusy"));
            await capture.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            capture.TryCapture(targets);
            JsonElement skipped = await WaitForStage(directory, "DiagnosticCoverage", row => Has(row.GetProperty("coverage"), "Reason", "CaptureCooldown"));
            Assert.True(skipped.GetProperty("coverage").GetProperty("LastRemainingCooldownMs").GetDouble() > 0);
            Assert.Equal(71, skipped.GetProperty("coverage").GetProperty("LastOperation").GetInt64());
        }
        finally { capture.Dispose(); await log.StopAsync(); }
    }

    [Fact]
    public async Task Capture_EndedTargetsAndExcessTargets_ReportsCoverageWithoutSilentDrop()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "capture-bound-test");
        using DiagnosticStackCapture capture = new(log);
        DiagnosticStackTarget ended = new(DiagnosticStage.RenderExecution, 1, 1, 0, int.MaxValue);
        ended.Lifetime.End();
        try
        {
            capture.TryCapture([ended]);
            await WaitForStage(directory, "DiagnosticCoverage", row => Has(row.GetProperty("coverage"), "Reason", "CaptureNoActiveTargets"));
            capture.TryCapture(Enumerable.Range(1, 129)
                .Select(id => new DiagnosticStackTarget(DiagnosticStage.RenderExecution, 1, id, 0, int.MaxValue)).ToArray());
            JsonElement skipped = await WaitForStage(directory, "DiagnosticCoverage", row => Has(row.GetProperty("coverage"), "Reason", "CaptureTargetLimit"));
            Assert.Equal(1, skipped.GetProperty("coverage").GetProperty("LastTargetCount").GetInt32());
            await capture.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        finally { capture.Dispose(); await log.StopAsync(); }
        Assert.Equal(128, directory.Read().Count(row => Has(row, "stage", "ManagedStackSample")));
    }

    [Fact]
    public async Task Writer_DedicatedThread_FlushesSingleRecordBeforeStop()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "writer-thread-test");
        int producer = DiagnosticThreadProbe.NativeThreadId;
        try
        {
            log.Mark(DiagnosticStage.ManagedEntry);
            await WaitForStage(directory, "ManagedEntry");
            JsonElement header = directory.Read().Single(row => row.TryGetProperty("schema", out _));
            Assert.False(header.GetProperty("writer_thread_pool").GetBoolean());
            Assert.NotEqual(producer, header.GetProperty("writer_native_thread_id").GetInt32());
        }
        finally { await log.DisposeAsync(); }
    }

    [Fact]
    public async Task FullQueue_OverflowAndCoverage_AreDurableBeforeStop()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "overflow-live-test", capacity: 1);
        try
        {
            for (int index = 0; index < 10000; index++) log.Mark(DiagnosticStage.WindowLoaded);
            Assert.True(log.DroppedEntries > 0);
            for (int index = 0; index < 1000; index++)
                log.ReportCoverage(DiagnosticCoverageReason.ObservationCapacity, DiagnosticStage.DocumentDecode);
            await WaitForStage(directory, "LogOverflow");
            await WaitForStage(directory, "DiagnosticCoverage", row => row.GetProperty("coverage").GetProperty("AttemptsTotal").GetInt64() == 1000);
        }
        finally { await log.StopAsync(); }
        Assert.Equal(log.DroppedEntries, directory.Read().Last(row => Has(row, "stage", "LogOverflow")).GetProperty("dropped").GetInt64());
    }

    [Fact]
    public async Task QuietScope_WhilePending_StillReportsActualWorkerAndParent()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "quiet-worker-test");
        log.EnableStallMonitoring();
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        DiagnosticLog.Current = log;
        CoreDiagnosticBridge.Start();
        using ManualResetEventSlim release = new();
        int workerId = 0;
        Task? worker = null;
        using DiagnosticOperation root = log.Begin(DiagnosticStage.DocumentOpen, 51, execution: DiagnosticExecutionKind.AsyncAwait);
        try
        {
            worker = Task.Run(() => CoreOperationTrace.Measure("DocumentDecode", () =>
            {
                workerId = DiagnosticThreadProbe.NativeThreadId;
                release.Wait();
                return true;
            }));
            JsonElement sample = await WaitForStage(directory, "SlowOperationSample",
                row => row.GetProperty("observation").GetProperty("PendingStage").GetString() == "DocumentDecode");
            Assert.Equal(root.Id, sample.GetProperty("parent_operation").GetInt64());
            Assert.Equal(workerId, sample.GetProperty("observation").GetProperty("OwnerNativeThreadId").GetInt32());
            Assert.Equal("Synchronous", sample.GetProperty("execution_kind").GetString());
            Assert.DoesNotContain(directory.Read(), row => Has(row, "stage", "DocumentDecode") && Has(row, "outcome", "Started"));
        }
        finally
        {
            release.Set();
            if (worker is not null) await worker.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            root.Succeed();
            root.Dispose();
            DiagnosticLog.Current = previous;
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task QuietFastScope_WithChild_RestoresContextWithoutFastOutput()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "quiet-fast-test");
        using (DiagnosticOperation root = log.Begin(DiagnosticStage.DocumentOpen, 42))
        {
            using (DiagnosticOperation quiet = log.Begin(DiagnosticStage.DocumentDecode, onlySlow: true))
            {
                Assert.Equal(quiet.Id, log.Context.Value.Operation);
                using DiagnosticOperation child = log.Begin(DiagnosticStage.DocumentFileStamp);
                child.Succeed();
            }
            Assert.Equal(root.Id, log.Context.Value.Operation);
            root.Succeed();
        }
        Assert.Equal(0, log.Context.Value.Operation);
        await log.StopAsync();
        Assert.DoesNotContain(directory.Read(), row => Has(row, "stage", "DocumentDecode"));
    }

    [Fact]
    public async Task AsyncScope_CompletesOnEntryThread_DoesNotClaimExclusiveWorkerCpu()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "async-kind-test");
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentFileBytes,
            execution: DiagnosticExecutionKind.AsyncAwait)) operation.Succeed();
        await log.StopAsync();
        JsonElement end = directory.Read().Single(row => Has(row, "outcome", "Succeeded"));
        Assert.Equal("AsyncAwait", end.GetProperty("execution_kind").GetString());
        Assert.False(end.TryGetProperty("owner_cpu_ms", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dispatcher_WithoutAnyOperation_ReportsBeforeRecoveryAndStopsCleanly(int round)
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "dispatcher-test");
        log.EnableStallMonitoring();
        using ManualResetEventSlim release = new();
        TaskCompletionSource<Dispatcher> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread ui = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using DiagnosticDispatcherWatch? watch = log.WatchDispatcher(dispatcher, round);
            dispatcher.BeginInvoke(new Action(() => { started.SetResult(dispatcher); BlockedStackFixture(release); }));
            Dispatcher.Run();
        })
        { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Dispatcher target = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            JsonElement sample = await WaitForStage(directory, "DispatcherUnresponsive");
            Assert.Equal(round, sample.GetProperty("window").GetInt32());
            Assert.True(sample.GetProperty("duration_ms").GetDouble() >= 1500);
            Assert.DoesNotContain(directory.Read(), row => Has(row, "stage", "DispatcherRecovered"));
            if (round == 2)
            {
                JsonElement currentStack = await WaitForStage(directory, "ManagedStackSample",
                    row => StackContains(row, nameof(BlockedStackFixture)));
                Assert.Equal(round, currentStack.GetProperty("window").GetInt32());
                Assert.False(release.IsSet);
            }
            release.Set();
            await WaitForStage(directory, "DispatcherRecovered");
        }
        finally
        {
            release.Set();
            target.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DispatcherWatch_DisposedWithQueuedPulse_DoesNotRetainOrReportClosedDispatcher()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "dispatcher-dispose-test");
        Thread ui = new(() =>
        {
            DiagnosticDispatcherWatch watch = new(log, Dispatcher.CurrentDispatcher, 1);
            long now = Stopwatch.GetTimestamp();
            watch.Poll(now);
            watch.Dispose();
            Assert.True(watch.IsStopped);
            Assert.Null(watch.Poll(now + Stopwatch.Frequency * 10));
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ui.Join(TimeSpan.FromSeconds(5)));
        await log.StopAsync();
        Assert.Empty(directory.Read());
    }

    [Fact]
    public async Task ManagedSampler_BlockedAndBusyThreads_RecordsCurrentStacksWithoutRawTraceFiles()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "managed-stack-test");
        using ManualResetEventSlim release = new();
        int blockedId = 0, busyId = 0;
        using CountdownEvent ready = new(2);
        Thread blocked = new(() => { blockedId = DiagnosticThreadProbe.NativeThreadId; ready.Signal(); BlockedStackFixture(release); }) { IsBackground = true };
        Thread busy = new(() => { busyId = DiagnosticThreadProbe.NativeThreadId; ready.Signal(); BusyStackFixture(release); }) { IsBackground = true };
        blocked.Start(); busy.Start();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await Task.Run(() => ManagedStackSampler.Capture(log,
                [new(DiagnosticStage.DocumentDecode, 11, 101, 100, blockedId),
                 new(DiagnosticStage.RenderExecution, 12, 202, 200, busyId)], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            await log.StopAsync();
            JsonElement[] samples = directory.Read().Where(row => Has(row, "stage", "ManagedStackSample")).ToArray();
            Assert.Contains(samples, row => StackContains(row, nameof(BlockedStackFixture)) && row.GetProperty("owner_native_thread_id").GetInt32() == blockedId);
            Assert.Contains(samples, row => StackContains(row, nameof(BusyStackFixture)) && row.GetProperty("owner_native_thread_id").GetInt32() == busyId);
            Assert.All(samples, row => Assert.InRange(row.GetProperty("sampled_stack").GetProperty("Frames").GetArrayLength(), 1, 32));
            Assert.Contains(samples, row => row.GetProperty("parent_operation").GetInt64() == 100);
            Assert.All(Directory.GetFiles(directory.Path), file => Assert.EndsWith(".jsonl", file, StringComparison.Ordinal));
            Assert.DoesNotContain(directory.Read(), row => row.GetRawText().Contains(directory.Path, StringComparison.Ordinal));
        }
        finally
        {
            release.Set();
            Assert.True(blocked.Join(TimeSpan.FromSeconds(5)));
            Assert.True(busy.Join(TimeSpan.FromSeconds(5)));
            await log.StopAsync();
        }
    }

    [Fact]
    public async Task ManagedSampler_UnobservedThread_ExplicitlyReportsMissingSample()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "missing-stack-test");
        await Task.Run(() => ManagedStackSampler.Capture(log,
            [new(DiagnosticStage.RenderExecution, 9, 19, 18, int.MaxValue)], TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken);
        await log.StopAsync();
        Assert.Contains(directory.Read(), row => Has(row, "stage", "ManagedStackSample") && Has(row, "outcome", "Abandoned")
            && row.GetProperty("owner_native_thread_id").GetInt32() == int.MaxValue);
    }

    [Fact]
    public async Task StackCapture_OverlappingRequests_UsesSingleCaptureAndStopsOnDispose()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "single-capture-test");
        DiagnosticStackCapture capture = new(log);
        DiagnosticStackTarget[] targets = [new(DiagnosticStage.RenderExecution, 1, 7, 0, DiagnosticThreadProbe.NativeThreadId)];
        capture.TryCapture(targets);
        Task completion = capture.Completion;
        capture.TryCapture(targets);
        Assert.Same(completion, capture.Completion);
        capture.Dispose();
        capture.TryCapture(targets);
        await completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await log.StopAsync();
        Assert.Single(directory.Read(), row => Has(row, "stage", "ManagedStackCapture") && Has(row, "outcome", "Started"));
    }

    private static bool StackContains(JsonElement row, string method)
        => row.TryGetProperty("sampled_stack", out JsonElement stack)
            && stack.GetProperty("Frames").EnumerateArray().Any(frame => frame.GetString()?.Contains(method, StringComparison.Ordinal) == true);

    [Fact]
    public void StackLifetime_LateDeliveredSample_OnlyAcceptsOriginalOperationInterval()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        DiagnosticStackLifetime lifetime = new();
        DateTimeOffset during = DateTimeOffset.UtcNow;
        Assert.True(lifetime.IsActive);
        lifetime.End();
        Assert.False(lifetime.IsActive);
        Assert.True(lifetime.Contains(during));
        Assert.False(lifetime.Contains(before));
        Assert.False(lifetime.Contains(DateTimeOffset.UtcNow.AddSeconds(1)));
        lifetime.End();
        Assert.False(lifetime.Contains(DateTimeOffset.UtcNow.AddSeconds(1)));
    }

    [Fact]
    public async Task QuietSynchronousStall_AutomaticallyPersistsRealStackWhileStillBlocked()
    {
        using DetailDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "automatic-stack-test");
        log.EnableStallMonitoring();
        using ManualResetEventSlim release = new();
        Thread worker = new(() =>
        {
            using DiagnosticOperation operation = log.Begin(DiagnosticStage.RenderExecution, 23, onlySlow: true,
                execution: DiagnosticExecutionKind.Synchronous);
            BlockedStackFixture(release);
            operation.Succeed();
        })
        { IsBackground = true };
        worker.Start();
        try
        {
            JsonElement sample = await WaitForStage(directory, "ManagedStackSample", row => StackContains(row, nameof(BlockedStackFixture)));
            Assert.Equal(23, sample.GetProperty("window").GetInt32());
            Assert.False(release.IsSet);
            Assert.DoesNotContain(directory.Read(), row => Has(row, "stage", "RenderExecution") && Has(row, "outcome", "Succeeded"));
        }
        finally
        {
            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            await log.StopAsync();
            await log.ObserverCompletion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BlockedStackFixture(ManualResetEventSlim release) => release.Wait();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BusyStackFixture(ManualResetEventSlim release) { while (!release.IsSet) Thread.SpinWait(10000); }

    private static async Task<JsonElement> WaitForStage(DetailDirectory directory, string stage, Func<JsonElement, bool>? filter = null)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (JsonElement row in directory.Read())
                if (Has(row, "stage", stage) && (filter?.Invoke(row) ?? true)) return row;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("No durable diagnostic event: " + stage);
    }

    private static async Task<JsonElement> WaitForSample(DetailDirectory directory)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            JsonElement[] rows = directory.Read();
            foreach (JsonElement row in rows)
                if (Has(row, "stage", "SlowOperationSample")) return row;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("No durable slow-operation sample before completion");
    }

    private sealed class DetailDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WIMD-detail-tests-" + Guid.NewGuid().ToString("N"));

        internal JsonElement[] Read()
        {
            if (!Directory.Exists(Path)) return [];
            List<JsonElement> rows = [];
            foreach (string file in Directory.GetFiles(Path, "wimd-*.jsonl"))
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

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
