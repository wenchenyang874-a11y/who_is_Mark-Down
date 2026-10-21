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
    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string phase) => Source.StartActivity(phase);

    public static T Measure<T>(string phase, Func<T> operation)
    {
        using Activity? activity = Start(phase);
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
        try { return await operation().ConfigureAwait(false); }
        catch (Exception exception)
        {
            activity?.SetCustomProperty("exception", exception);
            throw;
        }
    }

    public static Task MeasureAsync(string phase, Func<Task> operation) => MeasureAsync(phase, async () =>
    {
        await operation().ConfigureAwait(false);
        return true;
    });

    // Fixed stage names plus numeric values only; never attach a source URL,
    // filename or HTTP headers to an Activity that the host could persist.
    public static void Value(string phase, long value)
    {
        using Activity? activity = Start("Value." + phase);
        activity?.SetCustomProperty("numeric_value", value);
    }
}
