using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace WhoIsMarkdown.App.Services;

// The runtime sample profiler captures managed threads (including external
// waits). It cannot supply native/kernel stacks or retroactive pre-Main events.
// Resolve only CLR method metadata; never use a SymbolReader or persist raw
// trace payloads containing module paths, command lines or user values.
internal static class ManagedStackSampler
{
    internal static void Capture(LocalDiagnosticLog log, IReadOnlyList<DiagnosticStackTarget> targets,
        CancellationToken shutdown, TimeSpan? sampleDuration = null)
    {
        long started = Stopwatch.GetTimestamp();
        long captureId = log.AllocateOperationId();
        log.Record(DiagnosticStage.ManagedStackCapture, DiagnosticOutcome.Started, operation: captureId);
        try
        {
            int lost = Collect(log, targets, sampleDuration, captureId, shutdown);
            log.Record(DiagnosticStage.ManagedStackCapture, DiagnosticOutcome.Succeeded, operation: captureId,
                duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds, value: lost);
        }
        catch (Exception exception)
        {
            bool canceled = shutdown.IsCancellationRequested;
            Exception error = !canceled && exception is OperationCanceledException
                ? new TimeoutException("Managed sampler attach deadline", exception) : exception;
            log.Record(DiagnosticStage.ManagedStackCapture, canceled ? DiagnosticOutcome.Canceled : DiagnosticOutcome.Failed,
                operation: captureId, duration: Stopwatch.GetElapsedTime(started).TotalMilliseconds, exception: error);
        }
    }

    // Keep the heavy dependency/JIT boundary inside the timed capture record.
    // Runtime sampling itself may briefly pause managed threads; the bounded
    // observer is diagnostic overhead, never part of a speedup claim.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Collect(LocalDiagnosticLog log, IReadOnlyList<DiagnosticStackTarget> targets,
        TimeSpan? sampleDuration, long captureId, CancellationToken shutdown)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        DiagnosticsClient client = new(Environment.ProcessId);
        using EventPipeSession session = client.StartEventPipeSessionAsync([
            new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
            new EventPipeProvider(SampleProfilerTraceEventParser.ProviderName, EventLevel.Informational),
        ], requestRundown: true, circularBufferMB: 8, deadline.Token).GetAwaiter().GetResult();
        using TraceLogEventSource source = TraceLog.CreateFromEventPipeSession(session);
        // Closing our stream unblocks Process() when collection/rundown stalls.
        // This stops this observer, not any document operation or external trace.
        using CancellationTokenRegistration abort = deadline.Token.Register(() =>
        {
            try { source.StopProcessing(); session.EventStream.Dispose(); }
            catch (Exception exception) when (exception is ObjectDisposedException or System.IO.IOException) { }
        });
        Dictionary<int, List<Captured>> samples = targets.Select(item => item.Thread).Distinct()
            .ToDictionary(thread => thread, _ => new List<Captured>());
        new SampleProfilerTraceEventParser(source).ThreadSample += data =>
        {
            if (!samples.TryGetValue(data.ThreadID, out List<Captured>? list) || list.Count >= 8) return;
            double elapsed = data.TimeStampRelativeMSec;
            if (list.Count != 0 && elapsed - list[^1].Elapsed < 200) return;
            if (data.CallStack() is { } stack) list.Add(new(stack, elapsed, data.TimeStamp.ToUniversalTime(), data.Type.ToString()));
        };
        // A dedicated controller must stop sampling even if the pool is starved.
        // No application/Dispatcher thread waits for it or for runtime rundown.
        Exception? stopFailure = null;
        Thread stopper = new(() =>
        {
            try
            {
                shutdown.WaitHandle.WaitOne(sampleDuration ?? TimeSpan.FromSeconds(2));
                using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(5));
                session.StopAsync(stopTimeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                stopFailure = exception;
                try { deadline.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        })
        { IsBackground = true, Name = "WIMD managed capture deadline" };
        stopper.Start();
        try
        {
            source.Process();
            if (!stopper.Join(TimeSpan.FromSeconds(6))) throw new TimeoutException();
            if (stopFailure is not null) throw new InvalidOperationException("Managed capture stop failed", stopFailure);
            deadline.Token.ThrowIfCancellationRequested();
            WriteSamples(log, targets, samples, captureId);
            return source.EventsLost;
        }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        {
            if (shutdown.IsCancellationRequested) throw new OperationCanceledException("Managed sampling shut down", exception, shutdown);
            throw new TimeoutException("Managed sampling deadline expired", exception);
        }
        finally
        {
            deadline.Cancel();
            stopper.Join(TimeSpan.FromSeconds(6));
        }
    }

    private static void WriteSamples(LocalDiagnosticLog log, IReadOnlyList<DiagnosticStackTarget> targets,
        Dictionary<int, List<Captured>> samples, long captureId)
    {
        foreach (DiagnosticStackTarget target in targets)
        {
            Captured[] captured = samples[target.Thread].Where(sample => target.Lifetime.Contains(sample.Utc)).ToArray();
            foreach (Captured sample in captured)
            {
                List<string> frames = [];
                int unresolved = 0;
                for (TraceCallStack? frame = sample.Stack; frame is not null && frames.Count < 32; frame = frame.Caller)
                {
                    string method = frame.CodeAddress.FullMethodName;
                    if (string.IsNullOrWhiteSpace(method)) { frames.Add("[unresolved]"); unresolved++; }
                    else frames.Add(method.Length > 1024 ? method[..1024] : method);
                }
                log.Record(DiagnosticStage.ManagedStackSample, DiagnosticOutcome.Point, target.Window,
                    target.Operation, parentOperation: target.Parent, ownerThread: target.Thread,
                    stack: new(captureId, target.Stage.ToString(), target.Thread, sample.Elapsed, sample.Utc, sample.Type,
                        [.. frames], unresolved, captured.Length));
            }
            // No samples is an explicit failure/coverage gap, not evidence
            // that a blocked thread had an empty stack or was doing no work.
            if (captured.Length == 0)
                log.Record(DiagnosticStage.ManagedStackSample, DiagnosticOutcome.Abandoned,
                    target.Window, target.Operation, parentOperation: target.Parent, ownerThread: target.Thread, value: 0);
        }
    }

    private sealed record Captured(TraceCallStack Stack, double Elapsed, DateTimeOffset Utc, string Type);
}
