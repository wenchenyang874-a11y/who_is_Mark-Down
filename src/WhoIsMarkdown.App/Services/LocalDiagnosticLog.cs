using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
internal sealed class LocalDiagnosticLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Channel<DiagnosticEntry> queue;
    private readonly Task writer;
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
        writer = Task.Run(WriteAsync);
    }

    internal string DirectoryPath { get; }
    internal Task Completion => writer;
    internal string? FailureType { get; private set; }
    internal long DroppedEntries => Interlocked.Read(ref dropped);
    internal DateTimeOffset? ManagedEntryUtc { get; set; }

    internal int AllocateWindowId() => Interlocked.Increment(ref nextWindow);

    internal DiagnosticOperation Begin(DiagnosticStage stage, int window = 0, bool onlySlow = false)
        => new(this, stage, window, Interlocked.Increment(ref nextOperation), onlySlow);

    internal void Mark(DiagnosticStage stage, int window = 0, long? value = null)
        => Record(stage, DiagnosticOutcome.Point, window, value: value);

    internal void Error(DiagnosticStage stage, Exception exception, int window = 0)
        => Record(stage, DiagnosticOutcome.Failed, window, exception: exception);

    internal void Record(DiagnosticStage stage, DiagnosticOutcome outcome, int window = 0,
        long operation = 0, double? duration = null, long? value = null, Exception? exception = null,
        double? jitMilliseconds = null, long? compiledMethods = null)
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
            window == 0 ? Context.Value.Window : window, operation, Context.Value.Operation, duration, value, exception?.GetType().FullName, exception?.HResult,
            exception?.InnerException?.GetType().FullName, exception?.InnerException?.HResult, errorFrames,
            jitMilliseconds, compiledMethods);
        if (!queue.Writer.TryWrite(entry)) Interlocked.Increment(ref dropped);
    }

    internal Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) queue.Writer.TryComplete();
        return writer;
    }

    private async Task WriteAsync()
    {
        FileStream? output = null;
        try
        {
            // Read process metadata and touch the log directory only off the UI
            // thread. No startup path probes, Flush(true), or per-event fsync.
            using Process process = Process.GetCurrentProcess();
            double launchToLoggerMs = Math.Max(0, (startedUtc - process.StartTime.ToUniversalTime()).TotalMilliseconds);
            Directory.CreateDirectory(DirectoryPath);
            int segment = 0;
            await foreach (DiagnosticEntry entry in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (output is null || output.Length >= maximumFileBytes)
                {
                    if (output is not null) await output.DisposeAsync().ConfigureAwait(false);
                    string name = string.Create(CultureInfo.InvariantCulture,
                        $"wimd-{startedUtc:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{session}-{segment++:D4}.jsonl");
                    output = new FileStream(Path.Combine(DirectoryPath, name), FileMode.CreateNew,
                        FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous);
                    await WriteLineAsync(output, new
                    {
                        schema = 1,
                        session,
                        pid = Environment.ProcessId,
                        version,
                        started_utc = startedUtc,
                        launch_to_logger_ms = launchToLoggerMs,
                        managed_entry_utc = ManagedEntryUtc,
                        process_to_managed_ms = ManagedEntryUtc is { } managed
                            ? Math.Max(0, (managed - process.StartTime.ToUniversalTime()).TotalMilliseconds) : (double?)null,
                        runtime = Environment.Version.ToString(),
                        processors = Environment.ProcessorCount,
                        timing = "process_elapsed_ms includes pre-managed launch; duration_ms measures host waits, not OS I/O termination",
                    }).ConfigureAwait(false);
                    TrimOldFiles();
                }
                await WriteLineAsync(output, new
                {
                    utc = entry.Utc,
                    process_elapsed_ms = launchToLoggerMs + entry.ElapsedMs,
                    log_elapsed_ms = entry.ElapsedMs,
                    stage = entry.Stage,
                    outcome = entry.Outcome,
                    window = entry.Window,
                    operation = entry.Operation,
                    parent_operation = entry.ParentOperation,
                    duration_ms = entry.DurationMs,
                    jit_ms = entry.JitMilliseconds,
                    compiled_methods = entry.CompiledMethods,
                    value = entry.Value,
                    error_type = entry.ErrorType,
                    hresult = entry.HResult,
                    inner_error_type = entry.InnerErrorType,
                    inner_hresult = entry.InnerHResult,
                    frames = entry.Frames,
                }).ConfigureAwait(false);
                // Batch bursts (e.g. startup) but persist a solitary begin record
                // as well, so a hang still leaves its last entered phase on disk.
                if (queue.Reader.Count == 0) await output.FlushAsync().ConfigureAwait(false);
            }
            if (output is not null && DroppedEntries > 0)
                await WriteLineAsync(output, new { stage = "LogOverflow", dropped = DroppedEntries }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or ArgumentException
            or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            FailureType = exception.GetType().Name;
            // Stop producers when the sink fails; do not fill a dead queue or
            // recursively report a logging error through the failed logger.
            Interlocked.Exchange(ref stopped, 1);
            queue.Writer.TryComplete();
        }
        finally
        {
            if (output is not null)
            {
                try { await output.DisposeAsync().ConfigureAwait(false); }
                catch (IOException exception) { FailureType = exception.GetType().Name; }
            }
        }
    }

    private static async Task WriteLineAsync<T>(FileStream output, T entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await output.WriteAsync(bytes).ConfigureAwait(false);
    }

    private void TrimOldFiles()
    {
        // Only our exact generated names qualify. Locked files belong to other
        // active windows/processes and must be left alone (best-effort retention).
        FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles("wimd-*.jsonl")
            .Where(file => IsOwnedFileName(file.Name))
            .OrderByDescending(file => file.CreationTimeUtc).ToArray();
        foreach (FileInfo file in files.Skip(retainedFiles))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsOwnedFileName(string name)
    {
        string[] parts = Path.GetFileNameWithoutExtension(name).Split('-');
        return parts.Length == 5 && parts[0] == "wimd"
            && DateTime.TryParseExact(parts[1], "yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && Guid.TryParseExact(parts[3], "N", out _) && parts[4].Length == 4
            && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private sealed record DiagnosticEntry(DateTimeOffset Utc, double ElapsedMs, string Stage, string Outcome,
        int Window, long Operation, long ParentOperation, double? DurationMs, long? Value, string? ErrorType, int? HResult,
        string? InnerErrorType, int? InnerHResult, string? Frames, double? JitMilliseconds, long? CompiledMethods);
}

internal sealed class DiagnosticOperation : IDisposable
{
    private readonly LocalDiagnosticLog log;
    private readonly DiagnosticStage stage;
    private readonly int window;
    private readonly long operation;
    private readonly bool onlySlow;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TimeSpan jitStarted = System.Runtime.JitInfo.GetCompilationTime();
    private readonly long methodsStarted = System.Runtime.JitInfo.GetCompiledMethodCount();
    private readonly (long Operation, int Window) previous;
    private DiagnosticOutcome outcome = DiagnosticOutcome.Abandoned;
    private Exception? error;
    private int disposed;

    internal DiagnosticOperation(LocalDiagnosticLog log, DiagnosticStage stage, int window, long operation, bool onlySlow)
    {
        this.log = log;
        this.stage = stage;
        previous = log.Context.Value;
        this.window = window == 0 ? previous.Window : window;
        this.operation = operation;
        this.onlySlow = onlySlow;
        if (!onlySlow)
        {
            log.Record(stage, DiagnosticOutcome.Started, this.window, operation);
            log.Context.Value = (operation, this.window);
        }
    }

    internal void Succeed() => outcome = DiagnosticOutcome.Succeeded;
    internal void Cancel() => outcome = DiagnosticOutcome.Canceled;
    internal void Fail(Exception exception)
    {
        error = exception;
        outcome = exception is OperationCanceledException ? DiagnosticOutcome.Canceled : DiagnosticOutcome.Failed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        double duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!onlySlow) log.Context.Value = previous;
        // Typing, scroll and mouse events must not produce a log storm. Slow
        // render waits and all failures remain visible; first renders opt out.
        if (!onlySlow || duration >= 500 || outcome == DiagnosticOutcome.Failed)
            log.Record(stage, outcome, window, operation, duration, exception: error,
                jitMilliseconds: Math.Max(0, (System.Runtime.JitInfo.GetCompilationTime() - jitStarted).TotalMilliseconds),
                compiledMethods: Math.Max(0, System.Runtime.JitInfo.GetCompiledMethodCount() - methodsStarted));
        error = null;
    }
}

internal enum DiagnosticOutcome { Point, Started, Succeeded, Failed, Canceled, Abandoned }

internal enum DiagnosticStage
{
    ManagedEntry, ApplicationAllocation, ApplicationResources, ApplicationRunRequested,
    WindowAllocation, WindowShow, WindowNativeCreated, WindowControllers, WindowDocumentBinding,
    RenderQueueWait, RenderThreadPoolWait, RenderSnapshot, RendererCreate, HtmlSanitizerCreate,
    MarkdownPipelineBuild, MarkdownAstParse, MarkdownHtmlDetection, MarkdownHtmlRender,
    MarkdownHtmlFilter, MarkdownImageResolve, MarkdownResultBuild,
    PreviewNavigationStarting, PreviewDomContentLoaded,
    SettingsProjection, ThemeAndFonts, ShortcutBindings, SettingsLayout,
    DocumentFileBytes, DocumentDecode, DocumentLineEndings, DocumentFileStamp, PreviewContentPrepare,
    DocumentCharacters, DocumentLines, PreviewBlocks, PreviewBodyCharacters,
    ApplicationConstructor, ApplicationStartup, RestoreSession, CaptureSession, WindowConstructor, WindowXaml,
    WindowLoaded, WindowFirstFrame, StartupContentReady, StartupPreviewDeferred, StartupPreviewReady,
    SettingsLoad, SettingsApply, SettingsWrite, SettingsFlush, BackgroundLoad, BackgroundImport,
    DocumentOpen, DocumentRead, DocumentApply, DocumentSave, DocumentReload, ExternalChangeCheck, RecentFileCheck,
    PreviewInitialize, PreviewResources, WebViewEnvironment, WebViewControl, WebViewScripts,
    PreviewRender, MarkdownParse, PreviewDelivery, PreviewReady, PreviewNavigation, MermaidRender, BrowserProcessFailed,
    ImageInsert, ImagePrepare, ImageUpload, ImageViewer, ImageViewerInitialize, ImageExport, PdfExport,
    WorkspaceOpen, WorkspaceRefresh, WorkspaceExpand, WorkspaceCreateFile, WorkspaceCreateDirectory,
    WorkspaceRename, WorkspaceDelete, UpdateCheck, UpdateDownload, UpdateInstall,
    TaskToggle, CodeCopy, ExternalLink, OpenNewWindow, OpenLogDirectory, HostScriptWait,
    ProcessWorkingSetBytes, ProcessCpuTotalMilliseconds, DispatcherSampleGap, LayoutChanged,
    WindowClosing, WindowClosed, ApplicationExit, DispatcherUnhandled, ProcessUnhandled, UnobservedTask,
}
