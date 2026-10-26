using System.Diagnostics;
using System.IO;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Production logging is activated by App only, not by constructing a Window in
/// an isolated test host. Tests may explicitly install their own local sink.
/// </summary>
internal static class DiagnosticLog
{
    internal static LocalDiagnosticLog? Current { get; set; }

    internal static void Start(DateTimeOffset? managedEntryUtc = null)
    {
        CoreDiagnosticBridge.Start();
        Current ??= new LocalDiagnosticLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WIMD", "Logs"),
            typeof(DiagnosticLog).Assembly.GetName().Version?.ToString() ?? "unknown");
        // Assembly callbacks can enqueue immediately. Set the process/entry
        // alignment before registering them, not after the writer's first row.
        if (managedEntryUtc is not null) Current.ManagedEntryUtc = managedEntryUtc;
        Current.EnableStallMonitoring();
    }

    internal static async Task<T> MeasureAsync<T>(DiagnosticStage stage, Func<Task<T>> action, int window = 0,
        bool onlySlow = false)
    {
        bool previousDetail = CoreDiagnosticBridge.Detailed.Value;
        if (!onlySlow && Current is not null) CoreDiagnosticBridge.Detailed.Value = true;
        using DiagnosticOperation? operation = Current?.Begin(stage, window, onlySlow, DiagnosticExecutionKind.AsyncAwait);
        Task<T>? pending = null;
        try
        {
            pending = Dispatch(action, operation, window, onlySlow);
            // A delegate often captures a Window. Do not keep that capture in
            // this async state machine after the underlying work has started.
            action = null!;
            T result = await pending.ConfigureAwait(true);
            operation?.Succeed();
            return result;
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
        finally
        {
            if (pending is not null) operation?.ResumeAwait(pending);
            pending = null;
            action = null!;
            CoreDiagnosticBridge.Detailed.Value = previousDetail;
        }
    }

    internal static async Task MeasureAsync(DiagnosticStage stage, Func<Task> action, int window = 0,
        bool onlySlow = false)
    {
        bool previousDetail = CoreDiagnosticBridge.Detailed.Value;
        if (!onlySlow && Current is not null) CoreDiagnosticBridge.Detailed.Value = true;
        using DiagnosticOperation? operation = Current?.Begin(stage, window, onlySlow, DiagnosticExecutionKind.AsyncAwait);
        Task? pending = null;
        try
        {
            pending = Dispatch(action, operation, window, onlySlow);
            action = null!;
            await pending.ConfigureAwait(true);
            operation?.Succeed();
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
        finally
        {
            if (pending is not null) operation?.ResumeAwait(pending);
            pending = null;
            action = null!;
            CoreDiagnosticBridge.Detailed.Value = previousDetail;
        }
    }

    private static T Dispatch<T>(Func<T> action, DiagnosticOperation? operation, int window, bool onlySlow) where T : Task
    {
        if (operation is null) return action();
        // Some async APIs block synchronously before returning their Task. Give
        // that real executing thread a separate scope, without changing awaits.
        using DiagnosticOperation? dispatch = Current?.Begin(DiagnosticStage.AsyncActionDispatch, window,
            onlySlow, DiagnosticExecutionKind.Synchronous);
        dispatch?.DetachFromAmbient();
        try
        {
            long started = Stopwatch.GetTimestamp();
            T pending = action();
            double dispatchMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            dispatch?.Succeed();
            operation.TrackAwait(pending, dispatchMs);
            return pending;
        }
        catch (Exception exception) { dispatch?.Fail(exception); throw; }
    }

    internal static T Measure<T>(DiagnosticStage stage, Func<T> action, int window = 0)
    {
        using DiagnosticOperation? operation = Current?.Begin(stage, window, execution: DiagnosticExecutionKind.Synchronous);
        try
        {
            T result = action();
            operation?.Succeed();
            return result;
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    internal static void Measure(DiagnosticStage stage, Action action, int window = 0)
        => Measure(stage, () => { action(); return true; }, window);
}
