using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WhoIsMarkdown.App.Services;

// Bug fix: scope-entry frames explain the caller but not where a thread is
// currently blocked. Load the sampling dependencies only after an actual stall;
// never initialize them on the startup/UI path. One short capture per minute,
// no dumps, disk trace, network symbols, manual Thread.Suspend or app data.
internal sealed class DiagnosticStackCapture(LocalDiagnosticLog log) : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private long previousCapture;
    private int running;
    private int stopped;
    private readonly object gate = new();
    private Task cancellation = Task.CompletedTask;
    private TaskCompletionSource? completed;
    internal Task Completion { get { lock (gate) return completed?.Task ?? Task.CompletedTask; } }

    internal void TryCapture(IReadOnlyList<DiagnosticStackTarget> targets)
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (stopped != 0) return;
            if (running != 0) { ReportSkipped(DiagnosticCoverageReason.CaptureBusy, targets); return; }
            double remaining = previousCapture == 0 ? 0 : 60000 - Stopwatch.GetElapsedTime(previousCapture, now).TotalMilliseconds;
            if (remaining > 0) { ReportSkipped(DiagnosticCoverageReason.CaptureCooldown, targets, remaining); return; }
            DiagnosticStackTarget[] candidates = targets.Where(item => item.Lifetime.IsActive)
                .DistinctBy(item => (item.Operation, item.Thread)).ToArray();
            if (candidates.Length == 0) { ReportSkipped(DiagnosticCoverageReason.CaptureNoActiveTargets, targets); return; }
            if (candidates.Length > 128) ReportSkipped(DiagnosticCoverageReason.CaptureTargetLimit, candidates[128..]);
            DiagnosticStackTarget[] selected = candidates.Take(128).ToArray();
            running = 1;
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previousCapture = now;
            Thread worker = new(() => Run(selected)) { IsBackground = true, Name = "WIMD bounded managed stack capture" };
            if (ExecutionContext.IsFlowSuppressed()) worker.Start();
            else { using (ExecutionContext.SuppressFlow()) worker.Start(); }
        }
    }

    private void ReportSkipped(DiagnosticCoverageReason reason, IReadOnlyList<DiagnosticStackTarget> targets, double remaining = 0)
    {
        if (targets.Count == 0) return;
        DiagnosticStackTarget last = targets[^1];
        log.ReportCoverage(reason, last.Stage, last.Window, last.Operation, last.Parent, last.Thread, targets.Count, remaining);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Run(DiagnosticStackTarget[] targets)
    {
        // Do not put EventPipe types in this class's fields or method signatures:
        // its dependency graph must remain unloaded during ordinary startup.
        try { ManagedStackSampler.Capture(log, targets, shutdown.Token); }
        catch (OperationCanceledException exception)
        {
            if (shutdown.IsCancellationRequested)
                log.Record(DiagnosticStage.ManagedStackCapture, DiagnosticOutcome.Canceled);
            else log.Error(DiagnosticStage.ManagedStackCapture, new TimeoutException("Managed sampler attach deadline", exception));
        }
        catch (Exception exception)
        {
            // Diagnostics are a fail-open observer, never an editor crash source.
            log.Error(DiagnosticStage.ManagedStackCapture, exception);
        }
        finally
        {
            Task cancel;
            bool dispose;
            TaskCompletionSource? completion;
            lock (gate)
            {
                running = 0;
                dispose = stopped != 0;
                cancel = cancellation;
                completion = completed;
            }
            // Never wait under the gate or on the closing UI thread. The active
            // sampler has already disposed its linked-token registrations here.
            if (dispose) { cancel.GetAwaiter().GetResult(); shutdown.Dispose(); }
            completion?.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopped != 0) return;
            stopped = 1;
            // Pipe-close callbacks can take time. CancelAsync marks the token
            // now but dispatches callbacks away from normal window shutdown.
            cancellation = shutdown.CancelAsync();
            if (running == 0) shutdown.Dispose();
        }
    }
}

internal sealed record DiagnosticStackTarget(DiagnosticStage Stage, int Window, long Operation, long Parent, int Thread)
{
    internal DiagnosticStackLifetime Lifetime { get; init; } = new();
}

// Only numeric lifetime data survives an operation. EventPipe events may arrive
// after completion: compare their original UTC timestamps, not delivery time, so
// a reused worker's next job is never attributed to the completed operation.
internal sealed class DiagnosticStackLifetime
{
    private readonly long started = DateTimeOffset.UtcNow.UtcTicks;
    private long ended;
    internal bool IsActive => Interlocked.Read(ref ended) == 0;
    internal void End() => Interlocked.CompareExchange(ref ended, DateTimeOffset.UtcNow.UtcTicks, 0);
    internal bool Contains(DateTimeOffset sample)
    {
        long end = Interlocked.Read(ref ended);
        return sample.UtcTicks >= started && (end == 0 || sample.UtcTicks <= end);
    }
}
internal sealed record DiagnosticStackSnapshot(long CaptureId, string PendingStage, int Thread, double TraceElapsedMs,
    DateTimeOffset SampleUtc, string SampleType, string[] Frames, int UnresolvedFrames, int SamplesForThread);
