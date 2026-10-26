using System.Collections.Concurrent;
using System.Diagnostics;

namespace WhoIsMarkdown.App.Services;

// Bug fix: a stalled operation never reaches its end log. A separate background
// thread records the original executing thread while that operation is pending.
// It is not a thread-pool timer (the pool itself may be starved). No Window,
// delegate, document, settings or exception object is retained by this monitor.
internal sealed class DiagnosticStallMonitor : IDisposable
{
    private readonly LocalDiagnosticLog log;
    private readonly ConcurrentDictionary<long, Pending> pending = new();
    private readonly object registrationGate = new();
    private readonly ConcurrentDictionary<int, DiagnosticDispatcherWatch> dispatchers = new();
    private readonly DiagnosticStackCapture stacks;
    private readonly AutoResetEvent changed = new(false);
    private readonly Thread worker;
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int stopped;
    internal Task Completion => Task.WhenAll(completed.Task, stacks.Completion);

    internal DiagnosticStallMonitor(LocalDiagnosticLog log)
    {
        this.log = log;
        stacks = new(log);
        worker = new Thread(Run) { IsBackground = true, Name = "WIMD local diagnostic observer" };
        if (ExecutionContext.IsFlowSuppressed()) worker.Start();
        else { using (ExecutionContext.SuppressFlow()) worker.Start(); }
    }

    internal bool Enter(long operation, DiagnosticStage stage, int window, long parent, int thread, double? cpu, string originFrames, long started,
        DiagnosticExecutionKind execution)
    {
        DiagnosticCoverageReason? skipped = null;
        bool wake = false;
        lock (registrationGate)
        {
            if (Volatile.Read(ref stopped) != 0) return false;
            // Capacity and the empty -> nonempty transition must be atomic.
            // Checking Count == 1 after concurrent inserts can miss both wakes.
            if (pending.Count >= 128) skipped = DiagnosticCoverageReason.ObservationCapacity;
            else
            {
                wake = pending.IsEmpty && dispatchers.IsEmpty;
                if (!pending.TryAdd(operation, new(stage, window, parent, thread, cpu, originFrames, started, execution)))
                    skipped = DiagnosticCoverageReason.ObservationDuplicate;
            }
        }
        if (skipped is { } reason) log.ReportCoverage(reason, stage, window, operation, parent, thread);
        else if (wake) Signal();
        return skipped is null;
    }

    internal void Leave(long operation)
    {
        lock (registrationGate)
            if (pending.TryRemove(operation, out Pending? item)) item.Lifetime.End();
    }

    internal DiagnosticDispatcherWatch? WatchDispatcher(System.Windows.Threading.Dispatcher dispatcher, int window)
    {
        if (Volatile.Read(ref stopped) != 0) return null;
        DiagnosticDispatcherWatch watch = new(log, dispatcher, window);
        if (dispatchers.TryGetValue(window, out DiagnosticDispatcherWatch? previous)) previous.Dispose();
        dispatchers[window] = watch;
        if (Volatile.Read(ref stopped) != 0) watch.Dispose();
        Signal();
        return watch;
    }

    internal void Checkpoint(long operation, XamlConstructionCheckpoint checkpoint)
    {
        if (pending.TryGetValue(operation, out Pending? item))
            Volatile.Write(ref item.Checkpoint, (int)checkpoint);
    }

    internal void AttachAwait(long operation, DiagnosticAwaitState state)
    {
        if (pending.TryGetValue(operation, out Pending? item)) Volatile.Write(ref item.AwaitState, state);
    }

    private void Run()
    {
        try
        {
            long previousTick = 0;
            while (Volatile.Read(ref stopped) == 0)
            {
                bool idle = pending.IsEmpty && dispatchers.IsEmpty;
                changed.WaitOne(idle ? Timeout.Infinite : 250);
                if (Volatile.Read(ref stopped) != 0) break;
                long now = Stopwatch.GetTimestamp();
                if (!idle && previousTick != 0 && Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds >= 2500)
                    log.Record(DiagnosticStage.ObserverSchedulingGap, DiagnosticOutcome.Point,
                        duration: Stopwatch.GetElapsedTime(previousTick, now).TotalMilliseconds);
                previousTick = now;
                List<DiagnosticStackTarget> targets = [];
                foreach ((int id, DiagnosticDispatcherWatch watch) in dispatchers)
                {
                    if (watch.IsStopped) { dispatchers.TryRemove(id, out _); continue; }
                    if (watch.Poll(now) is { } target) targets.Add(target);
                }
                Dictionary<int, DiagnosticThreadSnapshot> snapshots = [];
                foreach ((long operation, Pending item) in pending)
                {
                    double age = Stopwatch.GetElapsedTime(item.Started).TotalMilliseconds;
                    DiagnosticAwaitState? awaitState = Volatile.Read(ref item.AwaitState);
                    // Observe before the one-second slow probe. A completed task
                    // can be waiting on a busy UI with no active worker to sample.
                    if (awaitState?.ObserveCompletion() == true && age >= 500 && pending.ContainsKey(operation))
                        log.Record(DiagnosticStage.AsyncTaskCompletionObserved, DiagnosticOutcome.Point, item.Window,
                            operation, duration: age, parentOperation: item.Parent, awaitState: awaitState.Snapshot());
                    // A waiting async scope does not identify an executing worker.
                    // Capture it only when a synchronous child or UI pulse names
                    // the actual target thread. Do not sample an unrelated pool
                    // thread merely because that thread initiated an earlier await.
                    if (age >= 2000 && item.Execution == DiagnosticExecutionKind.Synchronous)
                        targets.Add(new(item.Stage, item.Window, operation, item.Parent, item.Thread) { Lifetime = item.Lifetime });
                    // Keep long hangs observable too: the first 20 probes are
                    // one second apart, then back off to once every 30 seconds.
                    // State samples do not partition or attribute all wall time.
                    if (age < item.NextProbeMs) continue;
                    item.Samples++;
                    item.NextProbeMs = age + (item.Samples < 20 ? 1000 : 30000);
                    // Nested phases often have the same entry thread. Query the
                    // OS once per thread/tick, not once for every ancestor scope.
                    if (!snapshots.TryGetValue(item.Thread, out DiagnosticThreadSnapshot? snapshot))
                    {
                        snapshot = DiagnosticThreadProbe.Read(item.Thread);
                        snapshots.Add(item.Thread, snapshot);
                    }
                    if (!pending.ContainsKey(operation) || Volatile.Read(ref stopped) != 0) continue;
                    log.Record(DiagnosticStage.SlowOperationSample, DiagnosticOutcome.Point, item.Window,
                        operation, duration: age, parentOperation: item.Parent,
                        observation: new(item.Stage.ToString(), item.Thread, item.CpuStart is { } begin && snapshot.ThreadCpuMs is { } cpuNow
                            ? Math.Max(0, cpuNow - begin) : null, snapshot.ProcessCpuMs, snapshot.ThreadState,
                            snapshot.WaitReason, snapshot.WorkingSetBytes, snapshot.AllocatedBytes, snapshot.ProbeMs, snapshot.Error,
                            ((XamlConstructionCheckpoint)Volatile.Read(ref item.Checkpoint)).ToString(), item.OriginFrames,
                            snapshot.ReadBytes, snapshot.WriteBytes, snapshot.Gen0, snapshot.Gen1, snapshot.Gen2,
                            snapshot.PendingWorkItems, snapshot.PoolThreads, item.Execution.ToString()), execution: item.Execution,
                        awaitState: awaitState?.Snapshot());
                }
                if (targets.Count > 0) stacks.TryCapture(targets);
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Error(DiagnosticStage.DiagnosticProbeFailed, exception);
        }
        finally { changed.Dispose(); completed.TrySetResult(); }
    }

    public void Dispose()
    {
        // Never join a potentially delayed OS query on the UI thread. The worker
        // is background-only; late results are rejected before enqueueing.
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        lock (registrationGate)
        {
            foreach (Pending item in pending.Values) item.Lifetime.End();
            pending.Clear();
        }
        foreach (DiagnosticDispatcherWatch watch in dispatchers.Values) watch.Dispose();
        dispatchers.Clear();
        stacks.Dispose();
        Signal();
    }

    private void Signal()
    {
        try { changed.Set(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class Pending(DiagnosticStage stage, int window, long parent, int thread, double? cpu, string originFrames, long started,
        DiagnosticExecutionKind execution)
    {
        internal readonly DiagnosticStage Stage = stage;
        internal readonly int Window = window;
        internal readonly long Parent = parent;
        internal readonly int Thread = thread;
        internal readonly double? CpuStart = cpu;
        internal readonly string OriginFrames = originFrames;
        internal readonly long Started = started;
        internal readonly DiagnosticExecutionKind Execution = execution;
        internal readonly DiagnosticStackLifetime Lifetime = new();
        internal double NextProbeMs = 1000;
        internal int Samples;
        internal int Checkpoint;
        internal DiagnosticAwaitState? AwaitState;
    }
}

internal sealed record DiagnosticObservation(string PendingStage, int OwnerNativeThreadId, double? OwnerCpuMs,
    double? ProcessCpuMs, string? ThreadState, string? WaitReason, long? WorkingSetBytes, long? AllocatedBytes,
    double ProbeMs, int? ProbeError, string XamlCheckpoint, string OriginFrames,
    ulong? ProcessReadBytes, ulong? ProcessWriteBytes, int? Gen0, int? Gen1, int? Gen2,
    long? PendingWorkItems, int? PoolThreads, string ExecutionKind)
{
    internal static DiagnosticObservation FromSnapshot(DiagnosticStage stage, int thread, DiagnosticThreadSnapshot snapshot,
        DiagnosticExecutionKind execution) => new(stage.ToString(), thread, null, snapshot.ProcessCpuMs,
            snapshot.ThreadState, snapshot.WaitReason, snapshot.WorkingSetBytes, snapshot.AllocatedBytes,
            snapshot.ProbeMs, snapshot.Error, XamlConstructionCheckpoint.None.ToString(), "",
            snapshot.ReadBytes, snapshot.WriteBytes, snapshot.Gen0, snapshot.Gen1, snapshot.Gen2,
            snapshot.PendingWorkItems, snapshot.PoolThreads, execution.ToString());
}
