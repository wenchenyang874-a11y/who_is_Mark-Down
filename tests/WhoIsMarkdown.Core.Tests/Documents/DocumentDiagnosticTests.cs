using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using WhoIsMarkdown.Core.Diagnostics;
using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.Core.Tests.Documents;

public sealed class DocumentDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_Twice_RecordsAllStagesAndPreservesExactBytes(bool bom)
    {
        using TemporaryDirectory directory = new();
        using Capture capture = new();
        string path = Path.Combine(directory.Path, "中文 空格.md");
        DocumentFileService service = new();
        for (int round = 0; round < 2; round++)
        {
            string text = "正文\r\n" + round;
            DocumentFileStamp stamp = await service.WriteAsync(new(path, text, bom), TestContext.Current.CancellationToken);
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] expected = bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
            Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(expected.Length, stamp.Length);
        }
        string[] phases = ["DocumentFileWrite", "DocumentWriteDirectoryCheck", "DocumentTemporaryOpen", "DocumentEncode",
            "DocumentBodyWrite", "DocumentBufferFlush", "DocumentDiskFlush", "DocumentTemporaryClose", "DocumentAtomicReplace",
            "DocumentInspect", "DocumentTemporaryCleanup"];
        foreach (string phase in phases) Assert.Equal(2, capture.Rows.Count(row => row.Stage == phase && row.Error is null));
        Assert.Equal(bom ? 2 : 0, capture.Rows.Count(row => row.Stage == "DocumentBomWrite"));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
        Assert.All(capture.Rows.Where(row => row.Stage == "DocumentDiskFlush"), row => Assert.True(row.Synchronous));
    }

    [Fact]
    public async Task Save_InvalidUtf16_RecordsEncodingFailureClosesAndCleansWithoutReplacing()
    {
        using TemporaryDirectory directory = new();
        using Capture capture = new();
        string path = Path.Combine(directory.Path, "keep.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DocumentFileException>(() => new DocumentFileService().WriteAsync(
            new(path, "\uD800", true), TestContext.Current.CancellationToken));
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentEncode" && row.Error is EncoderFallbackException);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentTemporaryClose" && row.Error is null);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentTemporaryCleanup" && row.Error is null);
        Assert.DoesNotContain(capture.Rows, row => row.Stage == "DocumentAtomicReplace");
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_TargetLocked_RecordsAtomicReplaceFailureAndPreservesOriginal()
    {
        using TemporaryDirectory directory = new();
        using Capture capture = new();
        string path = Path.Combine(directory.Path, "locked.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        DocumentFileException failure;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            failure = await Assert.ThrowsAsync<DocumentFileException>(() => new DocumentFileService().WriteAsync(
                new(path, "replacement", false), TestContext.Current.CancellationToken));
        // Windows can report a denied replacement as access denied or sharing
        // violation. Assert the exact propagated native failure, not one guessed type.
        Assert.Same(failure.InnerException, capture.Rows.Single(row => row.Stage == "DocumentAtomicReplace").Error);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentDiskFlush" && row.Error is null);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentTemporaryCleanup" && row.Error is null);
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_PreCanceled_RecordsCanceledWriteClosesAndCleans()
    {
        using TemporaryDirectory directory = new();
        using Capture capture = new();
        string path = Path.Combine(directory.Path, "canceled.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentFileService().WriteAsync(
            new(path, "replacement", false), new CancellationToken(true)));
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentBodyWrite" && row.Error is OperationCanceledException);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentFileWrite" && row.Error is OperationCanceledException);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentTemporaryClose" && row.Error is null);
        Assert.DoesNotContain(capture.Rows, row => row.Stage == "DocumentAtomicReplace");
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_MissingDirectory_RecordsValidationFailureWithoutOpeningTemp()
    {
        using TemporaryDirectory directory = new();
        using Capture capture = new();
        string path = Path.Combine(directory.Path, "missing", "note.md");
        await Assert.ThrowsAsync<DocumentFileException>(() => new DocumentFileService().WriteAsync(
            new(path, "body", false), TestContext.Current.CancellationToken));
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentWriteDirectoryCheck" && row.Error is DocumentFileException);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentFileWrite" && row.Error is DocumentFileException);
        Assert.DoesNotContain(capture.Rows, row => row.Stage == "DocumentTemporaryOpen");
    }

    [Fact]
    public async Task Save_CanceledAfterClose_RecordsCanceledRootAndDoesNotReplace()
    {
        using TemporaryDirectory directory = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using Capture capture = new(stopped: phase => { if (phase == "DocumentTemporaryClose") cancellation.Cancel(); });
        string path = Path.Combine(directory.Path, "cancel-before-replace.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DocumentFileService().WriteAsync(
            new(path, "replacement", false), cancellation.Token));
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentDiskFlush" && row.Error is null);
        Assert.Contains(capture.Rows, row => row.Stage == "DocumentFileWrite" && row.Error is OperationCanceledException);
        Assert.DoesNotContain(capture.Rows, row => row.Stage == "DocumentAtomicReplace");
        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Save_CleanupAlsoFails_RecordsBothFailuresWithoutMaskingOriginal()
    {
        using TemporaryDirectory directory = new();
        FileStream? held = null;
        using Capture capture = new(stopped: phase =>
        {
            if (phase == "DocumentTemporaryClose")
                held = new(Directory.EnumerateFiles(directory.Path, "*.tmp").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
        });
        string path = Path.Combine(directory.Path, "keep.md");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        try
        {
            DocumentFileException failure = await Assert.ThrowsAsync<DocumentFileException>(() => new DocumentFileService().WriteAsync(
                new(path, "replacement", false), TestContext.Current.CancellationToken));
            Assert.Same(failure.InnerException, capture.Rows.Single(row => row.Stage == "DocumentAtomicReplace").Error);
            Assert.NotNull(capture.Rows.Single(row => row.Stage == "DocumentTemporaryCleanup").Error);
            Assert.Single(Directory.EnumerateFiles(directory.Path, "*.tmp"));
            Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally { held?.Dispose(); }
    }

    [Fact]
    public async Task CoreAsyncObserver_ThrowingHooks_DoNotReplaceBusinessResultOrException()
    {
        using Capture capture = new(throwingObserver: true);
        Assert.Equal(17, await CoreOperationTrace.MeasureAsync("ObserverTest", () => Task.FromResult(17)));
        await CoreOperationTrace.MeasureAsync("ObserverTest", () => Task.CompletedTask);
        IOException expected = new("not logged");
        IOException actual = await Assert.ThrowsAsync<IOException>(() => CoreOperationTrace.MeasureAsync("ObserverTest",
            () => Task.FromException(expected)));
        Assert.Same(expected, actual);
        Assert.Equal(6, capture.Rows.Count(row => row.Stage == "DiagnosticAsyncObserverFailed"));
    }

    [Fact]
    public async Task CoreAsyncObserver_BothOverloads_ReportOriginalTaskAndResume()
    {
        using Capture capture = new();
        Task<int> generic = Task.FromResult(3);
        Task plain = Task.CompletedTask;
        Assert.Equal(3, await CoreOperationTrace.MeasureAsync("ObserverTest", () => generic));
        await CoreOperationTrace.MeasureAsync("ObserverTest", () => plain);
        Assert.Equal(2, capture.Observers.Count);
        Assert.Same(generic, capture.Observers[0].Returned);
        Assert.Same(plain, capture.Observers[1].Returned);
        Assert.All(capture.Observers, observer => Assert.Same(observer.Returned, observer.Resumed));
    }

    private sealed record Row(string Stage, Exception? Error, bool Synchronous);

    private sealed class Capture : IDisposable
    {
        private readonly AsyncLocal<bool> enabled = new();
        private readonly ActivityListener listener;
        internal ConcurrentQueue<Row> Rows { get; } = new();
        internal List<Observer> Observers { get; } = [];
        internal Capture(bool throwingObserver = false, Action<string>? stopped = null)
        {
            enabled.Value = true;
            listener = new()
            {
                ShouldListenTo = source => source.Name == CoreOperationTrace.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => enabled.Value ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStarted = activity =>
                {
                    if (!enabled.Value || activity.OperationName != "ObserverTest") return;
                    Observer observer = new(throwingObserver);
                    Observers.Add(observer);
                    activity.SetCustomProperty(CoreOperationTrace.AsyncObserverProperty, observer);
                },
                ActivityStopped = activity =>
                {
                    if (!enabled.Value) return;
                    Rows.Enqueue(new(activity.OperationName,
                        activity.GetCustomProperty("exception") as Exception, activity.GetTagItem("synchronous") is true));
                    stopped?.Invoke(activity.OperationName);
                }
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() { enabled.Value = false; listener.Dispose(); }
    }

    private sealed class Observer(bool throwing) : ICoreAsyncTraceObserver
    {
        internal Task? Returned { get; private set; }
        internal Task? Resumed { get; private set; }
        public void TaskReturned(Task task, double dispatchMilliseconds)
        {
            if (throwing) throw new InvalidOperationException("not logged");
            Returned = task;
        }
        public void CallerResumed(Task task)
        {
            if (throwing) throw new InvalidOperationException("not logged");
            Resumed = task;
        }
    }
}
