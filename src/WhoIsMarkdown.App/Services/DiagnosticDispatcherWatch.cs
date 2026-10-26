using System.Diagnostics;
using System.Windows.Threading;

namespace WhoIsMarkdown.App.Services;

// Bug fix: a DispatcherTimer cannot report a frozen dispatcher until it recovers.
// The independent diagnostic thread posts at most one heartbeat, even when no
// operation is registered. No Window is captured; disposal aborts a queued pulse.
internal sealed class DiagnosticDispatcherWatch : IDisposable
{
    private readonly WeakReference<Dispatcher> dispatcher;
    private readonly LocalDiagnosticLog log;
    private readonly int window;
    private readonly int thread;
    private readonly object gate = new();
    private DispatcherOperation? pulse;
    private DiagnosticStackLifetime? lifetime;
    private long posted;
    private long acknowledged;
    private long nextPost;
    private double nextReport = 1500;
    private bool reported;
    private bool stopped;

    internal DiagnosticDispatcherWatch(LocalDiagnosticLog log, Dispatcher dispatcher, int window)
    {
        dispatcher.VerifyAccess();
        this.log = log;
        this.dispatcher = new(dispatcher);
        this.window = window;
        thread = DiagnosticThreadProbe.NativeThreadId;
    }

    internal DiagnosticStackTarget? Poll(long now)
    {
        double reportAge = 0;
        DiagnosticStackTarget? result;
        lock (gate)
        {
            if (stopped) return null;
            if (posted != 0 && acknowledged != 0)
            {
                if (reported)
                    log.Record(DiagnosticStage.DispatcherRecovered, DiagnosticOutcome.Point, window,
                        duration: Stopwatch.GetElapsedTime(posted, acknowledged).TotalMilliseconds, ownerThread: thread);
                posted = acknowledged = 0;
                lifetime = null;
                pulse = null;
                reported = false;
                nextReport = 1500;
                nextPost = now + Stopwatch.Frequency;
            }
            if (posted == 0 && now >= nextPost)
            {
                if (!dispatcher.TryGetTarget(out Dispatcher? target) || target.HasShutdownStarted)
                {
                    stopped = true;
                    return null;
                }
                posted = now;
                lifetime = new();
                try { pulse = target.BeginInvoke(DispatcherPriority.Send, new Action(Acknowledge)); }
                catch (InvalidOperationException) { stopped = true; return null; }
            }
            if (posted == 0) return null;
            double age = Stopwatch.GetElapsedTime(posted, now).TotalMilliseconds;
            if (age >= nextReport)
            {
                nextReport = age + (age < 20000 ? 1000 : 30000);
                reported = true;
                reportAge = age;
            }
            result = age >= 2000 && lifetime is { } pendingLifetime
                ? new(DiagnosticStage.DispatcherUnresponsive, window, 0, 0, thread) { Lifetime = pendingLifetime } : null;
        }
        // Never query the OS while holding the gate used by the UI heartbeat.
        // A delayed diagnostic query must not become the UI stall being measured.
        if (reportAge != 0)
        {
            DiagnosticThreadSnapshot snapshot = DiagnosticThreadProbe.Read(thread);
            if (!IsStopped)
                log.Record(DiagnosticStage.DispatcherUnresponsive, DiagnosticOutcome.Point, window,
                    duration: reportAge, ownerThread: thread,
                    observation: DiagnosticObservation.FromSnapshot(DiagnosticStage.DispatcherUnresponsive,
                        thread, snapshot, DiagnosticExecutionKind.Synchronous));
        }
        return result;
    }

    private void Acknowledge()
    {
        lock (gate)
        {
            if (!stopped)
            {
                acknowledged = Stopwatch.GetTimestamp();
                lifetime?.End();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
            lifetime?.End();
            pulse?.Abort();
            pulse = null;
        }
    }

    internal bool IsStopped { get { lock (gate) return stopped; } }
}
