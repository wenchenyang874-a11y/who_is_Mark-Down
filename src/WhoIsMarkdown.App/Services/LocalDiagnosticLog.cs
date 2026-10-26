using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Bug fix: slow production starts had no durable phase timings. Producers only
/// enqueue bounded, typed records; a single worker batches local JSONL writes.
/// Never accept messages, paths, URLs, document text, clipboard data or secrets.
/// Logging failures must not change editing, crash handling or shutdown behavior.
/// </summary>
internal sealed partial class LocalDiagnosticLog : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Channel<DiagnosticEntry> queue;
    private readonly TaskCompletionSource writerCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutoResetEvent writerReady = new(false);
    private readonly DiagnosticCoverageTracker coverage = new();
    private readonly long origin = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly int maximumFileBytes;
    private readonly int retainedFiles;
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly string version;
    private long nextOperation;
    private long dropped;
    private int nextWindow;
    private int stopped;
    private DiagnosticStallMonitor? stallMonitor;
    private DiagnosticAssemblyObserver? assemblyObserver;
    private readonly object observerGate = new();
    internal AsyncLocal<(long Operation, int Window)> Context { get; } = new();

    internal LocalDiagnosticLog(string directory, string version, int capacity = 2048,
        int maximumFileBytes = 2 * 1024 * 1024, int retainedFiles = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileBytes, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        DirectoryPath = directory;
        this.version = version;
        this.maximumFileBytes = maximumFileBytes;
        this.retainedFiles = retainedFiles;
        queue = Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        // A thread-pool writer cannot persist evidence of thread-pool starvation.
        // Keep the entire wait/serialize/write/flush path on one dedicated thread.
        Thread writer = new(WriteLoop) { IsBackground = true, Name = "WIMD local diagnostic writer" };
        if (ExecutionContext.IsFlowSuppressed()) writer.Start();
        else { using (ExecutionContext.SuppressFlow()) writer.Start(); }
    }

    internal string DirectoryPath { get; }
    internal Task Completion => writerCompleted.Task;
    internal string? FailureType { get; private set; }
    internal long DroppedEntries => Interlocked.Read(ref dropped);
    internal DateTimeOffset? ManagedEntryUtc { get; set; }

    internal void EnableStallMonitoring()
    {
        lock (observerGate)
        {
            if (Volatile.Read(ref stopped) != 0) return;
            stallMonitor ??= new DiagnosticStallMonitor(this);
            assemblyObserver ??= new DiagnosticAssemblyObserver(this);
        }
    }

    private void StopObservers()
    {
        lock (observerGate)
        {
            stallMonitor?.Dispose();
            assemblyObserver?.Dispose();
        }
    }

    internal Task ObserverCompletion => stallMonitor?.Completion ?? Task.CompletedTask;

    internal DiagnosticDispatcherWatch? WatchDispatcher(System.Windows.Threading.Dispatcher dispatcher, int window)
        => stallMonitor?.WatchDispatcher(dispatcher, window);

    internal void Observe(long operation, DiagnosticStage stage, int window, long parent, int thread, double? cpu, long started,
        DiagnosticExecutionKind execution, bool captureOrigin)
    {
        if (stallMonitor is not { } monitor) return;
        string frames = !captureOrigin ? "" : string.Join(" <- ", new StackTrace(2, false).GetFrames().Take(8)
            .Select(frame => frame.GetMethod()).Where(method => method is not null)
            .Select(method => method!.DeclaringType?.FullName + "." + method.Name));
        monitor.Enter(operation, stage, window, parent, thread, cpu, frames, started, execution);
    }

    internal void CompleteObservation(long operation) => stallMonitor?.Leave(operation);

    internal void ObserveAwait(long operation, DiagnosticAwaitState state) => stallMonitor?.AttachAwait(operation, state);

    internal void SetXamlCheckpoint(XamlConstructionCheckpoint checkpoint)
        => stallMonitor?.Checkpoint(Context.Value.Operation, checkpoint);

    internal int AllocateWindowId() => Interlocked.Increment(ref nextWindow);
    internal long AllocateOperationId() => Interlocked.Increment(ref nextOperation);

    internal DiagnosticOperation Begin(DiagnosticStage stage, int window = 0, bool onlySlow = false,
        DiagnosticExecutionKind execution = DiagnosticExecutionKind.Unspecified)
        => new(this, stage, window, AllocateOperationId(), onlySlow, execution);

    internal void Mark(DiagnosticStage stage, int window = 0, long? value = null)
        => Record(stage, DiagnosticOutcome.Point, window, value: value);

    internal void Error(DiagnosticStage stage, Exception exception, int window = 0)
        => Record(stage, DiagnosticOutcome.Failed, window, exception: exception);

    internal void Record(DiagnosticStage stage, DiagnosticOutcome outcome, int window = 0,
        long operation = 0, double? duration = null, long? value = null, Exception? exception = null,
        double? jitMilliseconds = null, long? compiledMethods = null, long? parentOperation = null,
        DiagnosticObservation? observation = null, int? ownerThread = null, double? ownerCpuMs = null,
        DiagnosticAssembly? assembly = null, DiagnosticExecutionKind? execution = null,
        DiagnosticStackSnapshot? stack = null, DiagnosticAwaitSnapshot? awaitState = null,
        DiagnosticScriptWaitSnapshot? scriptWait = null)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        // Deliberately omit Exception.Message/ToString, source file names and
        // arbitrary extra fields: they routinely contain paths and API secrets.
        string? errorFrames = exception is null ? null : string.Join(" <- ",
            new StackTrace(exception, false).GetFrames().Take(8)
                .Select(frame => frame.GetMethod())
                .Where(method => method is not null)
                .Select(method => method!.DeclaringType?.FullName + "." + method.Name));
        DiagnosticEntry entry = new(DateTimeOffset.UtcNow,
            Stopwatch.GetElapsedTime(origin).TotalMilliseconds, stage.ToString(), outcome.ToString(),
            window == 0 ? Context.Value.Window : window, operation, parentOperation ?? Context.Value.Operation, duration, value, exception?.GetType().FullName, exception?.HResult,
            exception?.InnerException?.GetType().FullName, exception?.InnerException?.HResult, errorFrames,
            jitMilliseconds, compiledMethods, Environment.CurrentManagedThreadId, DiagnosticThreadProbe.NativeThreadId,
            ownerThread, ownerCpuMs, observation, assembly?.ToString(), execution?.ToString(), stack, awaitState, scriptWait);
        if (!queue.Writer.TryWrite(entry))
        {
            if (Volatile.Read(ref stopped) != 0) return;
            Interlocked.Increment(ref dropped);
        }
        SignalWriter();
    }

    internal void ReportCoverage(DiagnosticCoverageReason reason, DiagnosticStage stage, int window = 0,
        long operation = 0, long parent = 0, int thread = 0, int targets = 1, double remainingMs = 0)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        if (coverage.Add(reason, stage, window, operation, parent, thread, targets, remainingMs)) SignalWriter();
    }

    private void SignalWriter()
    {
        try { writerReady.Set(); }
        catch (ObjectDisposedException) { }
    }

    internal Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0)
        {
            StopObservers();
            queue.Writer.TryComplete();
            SignalWriter();
        }
        return writerCompleted.Task;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private sealed record DiagnosticEntry(DateTimeOffset Utc, double ElapsedMs, string Stage, string Outcome,
        int Window, long Operation, long ParentOperation, double? DurationMs, long? Value, string? ErrorType, int? HResult,
        string? InnerErrorType, int? InnerHResult, string? Frames, double? JitMilliseconds, long? CompiledMethods,
        int ManagedThreadId, int NativeThreadId, int? OwnerThread, double? OwnerCpuMs, DiagnosticObservation? Observation,
        string? Assembly, string? Execution, DiagnosticStackSnapshot? Stack, DiagnosticAwaitSnapshot? AwaitState,
        DiagnosticScriptWaitSnapshot? ScriptWait);
}

internal sealed class DiagnosticOperation : IDisposable, WhoIsMarkdown.Core.Diagnostics.ICoreAsyncTraceObserver
{
    internal long Id => operation;
    private readonly LocalDiagnosticLog log;
    private readonly DiagnosticStage stage;
    private readonly int window;
    private readonly long operation;
    private readonly bool onlySlow;
    private readonly DiagnosticExecutionKind execution;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan jitStarted = System.Runtime.JitInfo.GetCompilationTime();
    private readonly long methodsStarted = System.Runtime.JitInfo.GetCompiledMethodCount();
    private readonly int ownerThread = DiagnosticThreadProbe.NativeThreadId;
    private readonly double? cpuStarted = DiagnosticThreadProbe.CurrentThreadCpuMilliseconds();
    private readonly (long Operation, int Window) previous;
    private DiagnosticOutcome outcome = DiagnosticOutcome.Abandoned;
    private Exception? error;
    private DiagnosticAwaitState? awaitState;
    internal DiagnosticScriptWaitSnapshot? ScriptWait { get; set; }
    private int disposed;

    internal DiagnosticOperation(LocalDiagnosticLog log, DiagnosticStage stage, int window, long operation, bool onlySlow,
        DiagnosticExecutionKind execution)
    {
        this.log = log;
        this.stage = stage;
        previous = log.Context.Value;
        this.window = window == 0 ? previous.Window : window;
        this.operation = operation;
        this.onlySlow = onlySlow;
        this.execution = execution;
        if (!onlySlow)
        {
            log.Record(stage, DiagnosticOutcome.Started, this.window, operation, execution: execution);
        }
        // Suppress fast output, not live observation. A slow background parse
        // must be observable before it returns even on subsequent keystrokes.
        log.Context.Value = (operation, this.window);
        log.Observe(operation, stage, this.window, previous.Operation, ownerThread, cpuStarted, started,
            execution, captureOrigin: !onlySlow);
    }

    internal void Succeed() => outcome = DiagnosticOutcome.Succeeded;

    internal void DetachFromAmbient()
    {
        // Keep observing the synchronous thread, but let async children inherit
        // the parent operation which will remain alive across their awaits.
        if (log.Context.Value.Operation == operation) log.Context.Value = previous;
    }

    internal void TrackAwait(Task task, double dispatchMs)
    {
        awaitState = new(task, dispatchMs);
        log.ObserveAwait(operation, awaitState);
        if (!onlySlow || dispatchMs >= 500)
            log.Record(DiagnosticStage.AsyncTaskReturned, DiagnosticOutcome.Point, window, operation,
                parentOperation: previous.Operation, awaitState: awaitState.Snapshot());
    }

    internal void ResumeAwait(Task task) => awaitState?.Resume(task);

    void WhoIsMarkdown.Core.Diagnostics.ICoreAsyncTraceObserver.TaskReturned(Task task, double dispatchMilliseconds)
        => TrackAwait(task, dispatchMilliseconds);

    void WhoIsMarkdown.Core.Diagnostics.ICoreAsyncTraceObserver.CallerResumed(Task task) => ResumeAwait(task);
    internal void Cancel() => outcome = DiagnosticOutcome.Canceled;
    internal void Fail(Exception exception)
    {
        error = exception;
        outcome = exception is OperationCanceledException ? DiagnosticOutcome.Canceled : DiagnosticOutcome.Failed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        log.CompleteObservation(operation);
        double duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        log.Context.Value = previous;
        // Typing, scroll and mouse events must not produce a log storm. Slow
        // render waits and all failures remain visible; first renders opt out.
        if (!onlySlow || duration >= 500 || outcome == DiagnosticOutcome.Failed
            || (outcome == DiagnosticOutcome.Canceled && stage is (DiagnosticStage.RenderQueueWait
                or DiagnosticStage.RenderRequest or DiagnosticStage.RenderWorkAwait or DiagnosticStage.HostScriptWait
                or DiagnosticStage.HostScriptDispatch or DiagnosticStage.HostScriptPollCall or DiagnosticStage.HostScriptPollDelay)))
            log.Record(stage, outcome, window, operation, duration, exception: error,
                jitMilliseconds: Math.Max(0, (System.Runtime.JitInfo.GetCompilationTime() - jitStarted).TotalMilliseconds),
                compiledMethods: Math.Max(0, System.Runtime.JitInfo.GetCompiledMethodCount() - methodsStarted),
                ownerThread: ownerThread,
                // Async completions must not subtract different threads' CPU.
                ownerCpuMs: execution != DiagnosticExecutionKind.AsyncAwait && DiagnosticThreadProbe.NativeThreadId == ownerThread && cpuStarted is { } start
                    && DiagnosticThreadProbe.CurrentThreadCpuMilliseconds() is { } end ? Math.Max(0, end - start) : null,
                parentOperation: previous.Operation, execution: execution, awaitState: awaitState?.Snapshot(), scriptWait: ScriptWait);
        error = null;
    }
}

internal enum DiagnosticOutcome { Point, Started, Succeeded, Failed, Canceled, Abandoned }
internal enum DiagnosticExecutionKind { Unspecified, Synchronous, AsyncAwait }

internal enum DiagnosticStage
{
    SlowOperationSample, DiagnosticProbeFailed, XamlCheckpoint, StartupTraceLaunch, AssemblyLoaded,
    AsyncActionDispatch, AsyncTaskReturned, AsyncTaskCompletionObserved,
    CoreAsyncActionDispatch, DiagnosticAsyncObserverFailed,
    DocumentFileWrite, DocumentWriteDirectoryCheck, DocumentTemporaryOpen, DocumentEncode, DocumentEncodedBytes,
    DocumentBomWrite, DocumentBodyWrite, DocumentBufferFlush, DocumentDiskFlush, DocumentTemporaryClose,
    DocumentAtomicReplace, DocumentInspect, DocumentTemporaryCleanup,
    SettingsReadTransaction, SettingsSaveTransaction, SettingsMergeTransaction, SettingsLocate, SettingsExists,
    SettingsFallback, SettingsBytesRead, SettingsReadBytes, SettingsDecode, SettingsDeserialize, SettingsNormalize,
    SettingsMerge, SettingsInstanceLockWait, SettingsMutexCreate, SettingsMutexWait, SettingsMutexAbandoned, SettingsMutexRelease,
    SettingsDirectoryCreate, SettingsSerialize, SettingsWriteBytes, SettingsTemporaryOpen, SettingsBytesWrite,
    SettingsDiskFlush, SettingsTemporaryClose, SettingsAtomicReplace, SettingsTemporaryCleanup,
    RenderRequest, RenderWorkAwait, HostScriptDispatch, HostScriptPollCall, HostScriptPollDelay,
    DispatcherUnresponsive, DispatcherRecovered, ObserverSchedulingGap, ManagedStackCapture, ManagedStackSample,
    ApplicationBootstrapEntered, WindowFileServiceCreate, WindowRenderWorkerCreate, WindowDocumentSessionCreate,
    WindowPlacement, WindowAppearanceController, WindowPerformanceController, WindowScrollController,
    WindowDataContext, WindowNewDocument, WindowEditorDocument,
    ManagedEntry, ApplicationAllocation, ApplicationResources, ApplicationRunRequested,
    WindowAllocation, WindowShow, WindowNativeCreated, WindowControllers, WindowDocumentBinding,
    RenderQueueWait, RenderThreadPoolWait, RenderSnapshot, RendererCreate, HtmlSanitizerCreate,
    RenderExecution, DocumentFileReadDispatch,
    MarkdownPipelineBuild, MarkdownAstParse, MarkdownHtmlDetection, MarkdownHtmlRender,
    MarkdownHtmlFilter, MarkdownImageResolve, MarkdownResultBuild,
    PreviewNavigationStarting, PreviewDomContentLoaded,
    SettingsProjection, ThemeAndFonts, ShortcutBindings, SettingsLayout,
    DocumentFileBytes, DocumentDecode, DocumentLineEndings, DocumentFileStamp, PreviewContentPrepare,
    DocumentCharacters, DocumentLines, PreviewBlocks, PreviewBodyCharacters,
    PreviewHeadingFold, PreviewHeadingFoldRefresh,
    ApplicationConstructor, ApplicationStartup, RestoreSession, CaptureSession, WindowConstructor, WindowXaml,
    WindowLoaded, WindowFirstFrame, StartupContentReady, StartupPreviewDeferred, StartupPreviewReady,
    SettingsLoad, SettingsApply, SettingsWrite, SettingsFlush, BackgroundLoad, BackgroundImport,
    DocumentOpen, DocumentRead, DocumentApply, DocumentSave, DocumentReload, ExternalChangeCheck, RecentFileCheck,
    PreviewInitialize, PreviewResources, WebViewEnvironment, WebViewControl, WebViewScripts,
    PreviewRender, MarkdownParse, PreviewDelivery, PreviewReady, PreviewNavigation, MermaidRender, BrowserProcessFailed,
    PreviewRendererPrepare,
    ImageInsert, ImagePrepare, ImageUpload, ImageViewer, ImageViewerInitialize, ImageExport, PdfExport,
    ImageRequestReceived, ImageRequestOrigin, ImageSourceKind, ImageSourceResolve, ImageContextTarget,
    ImageCacheLookup, ImageCacheHit, ImageCacheMiss, ImageCacheCapture, ImageCacheCaptureSkipped,
    ImageCacheWaitExpired, ImageViewerApply, ImageViewerReadyDispatch,
    ImageViewerRenderDeferred,
    ImageCacheIdentity,
    ImageHttpTimeout,
    ImageCacheResponseStream, ImageCacheResponseRead, ImageCacheStored, ImageCacheEvicted, ImageCacheCleared,
    ImageCacheDirectory, ImageCacheWrite, ImageCacheFlush, ImageCachePublish, ImageCacheBytes,
    ImageHttpHeaders, ImageHttpStatus, ImageHttpRedirect, ImageResponseStream, ImageDownloadBody,
    ImageFormatDetect, ImageVectorNormalize, ImageSourceCopy, ImageStreamRead, ImageStreamWrite, ImageBytes,
    ImageViewerAllocation, ImageViewerShow, ImageViewerWindowFirstFrame, ImageViewerEnvironment,
    ImageViewerControl, ImageViewerScripts, ImageViewerNavigation, ImageViewerDomReady,
    ImageViewerDecoded, ImageViewerFrameReady, ImageViewerReady, ImageViewerSuperseded, ImageViewerClosed,
    WorkspaceOpen, WorkspaceRefresh, WorkspaceExpand, WorkspaceCreateFile, WorkspaceCreateDirectory,
    WorkspaceRename, WorkspaceDelete, UpdateCheck, UpdateDownload, UpdateInstall,
    TaskToggle, CodeCopy, ExternalLink, OpenNewWindow, OpenLogDirectory, HostScriptWait,
    ProcessWorkingSetBytes, ProcessCpuTotalMilliseconds, DispatcherSampleGap, LayoutChanged,
    WindowClosing, WindowClosed, ApplicationExit, DispatcherUnhandled, ProcessUnhandled, UnobservedTask,
}
