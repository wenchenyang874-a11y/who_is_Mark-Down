using System.Diagnostics;

namespace WhoIsMarkdown.Core.Diagnostics;

/// <summary>
/// Fixed phase names only: no document text, path, URL or user settings. With no
/// listener ActivitySource is a no-op; the desktop host owns persistence. Separate
/// scheduling, first-use construction and parsing without a UI dependency in Core.
/// </summary>
public static class CoreOperationTrace
{
    public const string SourceName = "WIMD.Core";
    public const string AsyncObserverProperty = "async_observer";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly KeyValuePair<string, object?>[] SynchronousTags = [new("synchronous", true)];
    private static readonly KeyValuePair<string, object?>[] AsyncTags = [new("synchronous", false)];

    // The tag must exist at ActivityStarted, not be added afterwards. Otherwise
    // a listener mistakes an async initiator for the actual synchronous worker.
    public static Activity? Start(string phase, bool synchronous = false)
        => Source.StartActivity(phase, ActivityKind.Internal, default(ActivityContext),
            tags: synchronous ? SynchronousTags : AsyncTags);

    public static T Measure<T>(string phase, Func<T> operation)
    {
        using Activity? activity = Start(phase, synchronous: true);
        try { return operation(); }
        catch (Exception exception)
        {
            activity?.SetCustomProperty("exception", exception);
            throw;
        }
    }

    public static void Report(string phase, double elapsedMilliseconds)
    {
        using Activity? activity = Start("Aggregate." + phase);
        activity?.SetCustomProperty("elapsed_ms", elapsedMilliseconds);
    }

    public static async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> operation)
    {
        using Activity? activity = Start(phase);
        Task<T>? pending = null;
        try
        {
            pending = Dispatch(activity, operation, out double dispatchMs);
            operation = null!;
            NotifyAsync(activity, pending, dispatchMs, resumed: false);
            return await pending.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetCustomProperty("exception", exception);
            throw;
        }
        finally
        {
            if (pending is not null) NotifyAsync(activity, pending, 0, resumed: true);
            pending = null;
            operation = null!;
        }
    }

    public static async Task MeasureAsync(string phase, Func<Task> operation)
    {
        using Activity? activity = Start(phase);
        Task? pending = null;
        try
        {
            pending = Dispatch(activity, operation, out double dispatchMs);
            operation = null!;
            NotifyAsync(activity, pending, dispatchMs, resumed: false);
            await pending.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetCustomProperty("exception", exception);
            throw;
        }
        finally
        {
            if (pending is not null) NotifyAsync(activity, pending, 0, resumed: true);
            pending = null;
            operation = null!;
        }
    }

    // Separate the real executing thread before Task return from later waits.
    // No listener means no extra dispatch Activity, callbacks or stopwatch work.
    private static T Dispatch<T>(Activity? parent, Func<T> action, out double dispatchMs) where T : Task
    {
        dispatchMs = 0;
        if (parent is null) return action();
        using Activity? dispatch = Start("CoreAsyncActionDispatch", synchronous: true);
        // Async work outlives this synchronous dispatch span. Its continuations
        // must inherit the real async parent, not an already-ended dispatch span.
        Activity.Current = parent;
        long started = Stopwatch.GetTimestamp();
        try
        {
            T task = action();
            dispatchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return task;
        }
        catch (Exception exception) { dispatch?.SetCustomProperty("exception", exception); throw; }
    }

    private static void NotifyAsync(Activity? activity, Task task, double dispatchMs, bool resumed)
    {
        if (activity?.GetCustomProperty(AsyncObserverProperty) is not ICoreAsyncTraceObserver observer) return;
        try
        {
            if (resumed) observer.CallerResumed(task);
            else observer.TaskReturned(task, dispatchMs);
        }
        catch (Exception exception)
        {
            // A diagnostic observer must never replace the business result or
            // exception. Keep a fixed failure stage; no arbitrary metadata.
            try
            {
                using Activity? failure = Start("DiagnosticAsyncObserverFailed", synchronous: true);
                failure?.SetCustomProperty("exception", exception);
            }
            catch { /* Even a broken listener must not break saving. */ }
        }
    }

    // Fixed stage names plus numeric values only; never attach a source URL,
    // filename or HTTP headers to an Activity that the host could persist.
    public static void Value(string phase, long value)
    {
        using Activity? activity = Start("Value." + phase);
        activity?.SetCustomProperty("numeric_value", value);
    }
}
