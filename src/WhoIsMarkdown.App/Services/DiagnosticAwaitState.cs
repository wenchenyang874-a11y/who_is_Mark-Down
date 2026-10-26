using System.Diagnostics;

namespace WhoIsMarkdown.App.Services;

// Bug fix: an async scope's wall time includes synchronous dispatch, the task,
// and resuming its caller. Observe Task.Status without installing continuations
// or retaining its result/Window. Completion time is a sampled upper bound,
// never an exact native I/O completion timestamp.
internal sealed class DiagnosticAwaitState
{
    private readonly object gate = new();
    private readonly WeakReference<Task> task;
    private readonly double dispatchMs;
    private readonly DateTimeOffset returnedUtc = DateTimeOffset.UtcNow;
    private readonly int returnedThread = DiagnosticThreadProbe.NativeThreadId;
    private readonly string statusAtReturn;
    private string latestStatus;
    private long completionObserved;
    private DateTimeOffset? completionUtc;
    private readonly bool completedAtReturn;
    private long resumed;
    private DateTimeOffset? resumedUtc;
    private int? resumedThread;

    internal DiagnosticAwaitState(Task task, double dispatchMs)
    {
        this.task = new(task);
        this.dispatchMs = dispatchMs;
        TaskStatus status = task.Status;
        statusAtReturn = latestStatus = status.ToString();
        completedAtReturn = IsCompleted(status);
        if (completedAtReturn) NoteCompletion();
    }

    internal bool ObserveCompletion()
    {
        lock (gate)
        {
            if (resumed != 0 || completionObserved != 0) return false;
            if (!task.TryGetTarget(out Task? pending)) { latestStatus = "NotReachable"; return false; }
            TaskStatus status = pending.Status;
            latestStatus = status.ToString();
            if (!IsCompleted(status)) return false;
            NoteCompletion();
            return true;
        }
    }

    internal void Resume(Task completed)
    {
        lock (gate)
        {
            if (resumed != 0) return;
            latestStatus = completed.Status.ToString();
            resumed = Stopwatch.GetTimestamp();
            resumedUtc = DateTimeOffset.UtcNow;
            resumedThread = DiagnosticThreadProbe.NativeThreadId;
        }
    }

    internal DiagnosticAwaitSnapshot Snapshot()
    {
        lock (gate)
            return new(resumed != 0 ? "Resumed" : completionObserved != 0 ? "TaskCompletedAwaitingContinuation"
                    : latestStatus == "NotReachable" ? "TaskNotReachable" : "AwaitingTask",
                dispatchMs, returnedUtc, returnedThread, statusAtReturn, latestStatus,
                completionUtc, completionObserved == 0 ? "NotObservedBeforeResume"
                    : completedAtReturn ? "CompletedAtReturn" : "ObserverPollUpperBound",
                resumedUtc, resumedThread,
                resumed != 0 && completionObserved != 0
                    ? Math.Max(0, Stopwatch.GetElapsedTime(completionObserved, resumed).TotalMilliseconds) : null);
    }

    private void NoteCompletion()
    {
        completionObserved = Stopwatch.GetTimestamp();
        completionUtc = DateTimeOffset.UtcNow;
    }

    private static bool IsCompleted(TaskStatus status)
        => status is TaskStatus.RanToCompletion or TaskStatus.Canceled or TaskStatus.Faulted;
}

internal sealed record DiagnosticAwaitSnapshot(string Phase, double DispatchMs, DateTimeOffset ReturnedUtc,
    int ReturnedThread, string TaskStatusAtReturn, string LatestTaskStatus, DateTimeOffset? CompletionObservedUtc,
    string CompletionObservation, DateTimeOffset? ResumeUtc, int? ResumeThread, double? ObservationToResumeMs);
