using System.Diagnostics;

namespace WhoIsMarkdown.App.Services;

// Bug fix: coverage limits silently removed the very evidence needed to explain
// missing samples. Fixed-size numeric state bypasses the bounded event queue;
// the dedicated writer persists the first rejection and one-second aggregates.
internal sealed class DiagnosticCoverageTracker
{
    private readonly State[] states = Enum.GetValues<DiagnosticCoverageReason>().Select(_ => new State()).ToArray();

    internal bool Add(DiagnosticCoverageReason reason, DiagnosticStage stage, int window, long operation,
        long parent, int thread, int targets, double remainingMs)
    {
        State state = states[(int)reason];
        lock (state)
        {
            bool wake = state.Total == state.Reported;
            state.Total++;
            state.TargetAttempts += targets;
            state.Stage = stage;
            state.Window = window;
            state.Operation = operation;
            state.Parent = parent;
            state.Thread = thread;
            state.LastTargets = targets;
            state.RemainingMs = remainingMs;
            state.LastUtc = DateTimeOffset.UtcNow;
            return wake;
        }
    }

    internal bool HasPending => states.Any(state => { lock (state) return state.Total != state.Reported; });

    internal IEnumerable<DiagnosticCoverageSnapshot> TakeDue(bool force)
    {
        for (int index = 0; index < states.Length; index++)
        {
            State state = states[index];
            DiagnosticCoverageSnapshot? snapshot = null;
            lock (state)
            {
                if (state.Total == state.Reported || (!force && state.Reported != 0
                    && Stopwatch.GetElapsedTime(state.LastReport).TotalSeconds < 1)) continue;
                snapshot = new(((DiagnosticCoverageReason)index).ToString(), state.Total, state.Total - state.Reported,
                    state.TargetAttempts, state.Stage.ToString(), state.Window, state.Operation, state.Parent,
                    state.Thread, state.LastTargets, state.RemainingMs, state.LastUtc);
                state.Reported = state.Total;
                state.LastReport = Stopwatch.GetTimestamp();
            }
            yield return snapshot;
        }
    }

    private sealed class State
    {
        internal long Total, Reported, TargetAttempts, LastReport, Operation, Parent;
        internal DiagnosticStage Stage;
        internal int Window, Thread, LastTargets;
        internal double RemainingMs;
        internal DateTimeOffset LastUtc;
    }
}

internal enum DiagnosticCoverageReason
{
    ObservationCapacity, ObservationDuplicate, CaptureBusy, CaptureCooldown, CaptureNoActiveTargets, CaptureTargetLimit,
}

internal sealed record DiagnosticCoverageSnapshot(string Reason, long AttemptsTotal, long AttemptsSinceLastReport,
    long TargetAttemptsTotal, string LastPendingStage, int LastWindow, long LastOperation, long LastParentOperation,
    int LastNativeThreadId, int LastTargetCount, double LastRemainingCooldownMs, DateTimeOffset LastAttemptUtc);
