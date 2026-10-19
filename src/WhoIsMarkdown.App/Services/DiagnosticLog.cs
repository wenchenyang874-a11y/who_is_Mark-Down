using System.IO;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Production logging is activated by App only, not by constructing a Window in
/// an isolated test host. Tests may explicitly install their own local sink.
/// </summary>
internal static class DiagnosticLog
{
    internal static LocalDiagnosticLog? Current { get; set; }

    internal static void Start()
    {
        Current ??= new LocalDiagnosticLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WIMD", "Logs"),
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");
    }

    internal static async Task<T> MeasureAsync<T>(DiagnosticStage stage, Func<Task<T>> action, int window = 0,
        bool onlySlow = false)
    {
        using DiagnosticOperation? operation = Current?.Begin(stage, window, onlySlow);
        try
        {
            Task<T> pending = action();
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
    }

    internal static async Task MeasureAsync(DiagnosticStage stage, Func<Task> action, int window = 0,
        bool onlySlow = false)
    {
        using DiagnosticOperation? operation = Current?.Begin(stage, window, onlySlow);
        try
        {
            Task pending = action();
            action = null!;
            await pending.ConfigureAwait(true);
            operation?.Succeed();
        }
        catch (Exception exception)
        {
            operation?.Fail(exception);
            throw;
        }
    }

    internal static T Measure<T>(DiagnosticStage stage, Func<T> action, int window = 0)
    {
        using DiagnosticOperation? operation = Current?.Begin(stage, window);
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
