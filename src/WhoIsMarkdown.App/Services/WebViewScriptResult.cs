using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Reads the outcome of a page script that cannot report it synchronously.
///
/// Bug fix (silently skipped waits): WebView2's ExecuteScriptAsync does not await a
/// promise. A script written as an async IIFE therefore returns the serialization of the
/// pending promise — an empty object — so every host-side "await" of such a script
/// completed immediately. That silently turned three deliberate waits into no-ops: the
/// Mermaid idle wait, the PDF image settle wait, and the offscreen rasterizer's size
/// measurement. The page now publishes its outcome into a slot that the host polls.
///
/// The continuations stay on the calling synchronization context on purpose: the
/// offscreen rasterizer must remain on the WPF dispatcher thread between steps.
/// </summary>
internal static class WebViewScriptResult
{
    private const int PollIntervalMilliseconds = 40;

    /// <summary>
    /// Runs a script that publishes its outcome into a slot, then waits for that slot.
    /// </summary>
    /// <returns>The published value, or <see langword="null"/> when it never appeared.</returns>
    public static async Task<string?> RunAsync(
        CoreWebView2 core,
        string script,
        string slotExpression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotExpression);

        return await RunAsync(core.ExecuteScriptAsync, script, slotExpression, timeout, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Polls a page global until the script published a value.
    /// </summary>
    /// <returns>The published value, or <see langword="null"/> when it never appeared.</returns>
    public static async Task<string?> PollAsync(
        CoreWebView2 core,
        string slotExpression,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotExpression);

        return await PollAsync(core.ExecuteScriptAsync, slotExpression, timeout, cancellationToken).ConfigureAwait(true);
    }

    // The delegate seam tests the exact production wait algorithm without a
    // browser. It does not authorize new scripts or change STA/context affinity.
    internal static Task<string?> RunAsync(Func<string, Task<string>> execute, string script, string slotExpression,
        TimeSpan timeout, CancellationToken cancellationToken)
        => WaitAsync(execute, script, slotExpression, timeout, cancellationToken);

    internal static Task<string?> PollAsync(Func<string, Task<string>> execute, string slotExpression,
        TimeSpan timeout, CancellationToken cancellationToken)
        => WaitAsync(execute, null, slotExpression, timeout, cancellationToken);

    private static async Task<string?> WaitAsync(Func<string, Task<string>> execute, string? script,
        string slotExpression, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotExpression);
        Stopwatch elapsed = Stopwatch.StartNew();
        WaitCounters counters = new();
        using DiagnosticOperation? wait = DiagnosticLog.Current?.Begin(DiagnosticStage.HostScriptWait,
            onlySlow: !CoreDiagnosticBridge.Detailed.Value, execution: DiagnosticExecutionKind.AsyncAwait);
        try
        {
            // Run historically checks cancellation before an empty budget;
            // direct Poll returns null immediately for an empty budget.
            if (script is not null) cancellationToken.ThrowIfCancellationRequested();
            if (timeout <= TimeSpan.Zero) throw new TimeoutException();
            if (script is not null)
                await ExecuteAsync(execute, script, timeout, counters, initial: true, cancellationToken).ConfigureAwait(true);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                string result = await ExecuteAsync(execute, slotExpression, remaining,
                    counters, initial: false, cancellationToken).ConfigureAwait(true);

                string trimmed = result.Trim();
                if (!trimmed.Equals("null", StringComparison.Ordinal))
                {
                    counters.Outcome = "Ready";
                    wait?.Succeed();
                    return trimmed;
                }
                counters.NullResponses++;
                remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                long delayStarted = Stopwatch.GetTimestamp();
                try
                {
                    await DiagnosticLog.MeasureAsync(DiagnosticStage.HostScriptPollDelay,
                        () => Task.Delay(TimeSpan.FromMilliseconds(Math.Min(PollIntervalMilliseconds, remaining.TotalMilliseconds)),
                            cancellationToken), onlySlow: true).ConfigureAwait(true);
                }
                finally { counters.DelayMs += Stopwatch.GetElapsedTime(delayStarted).TotalMilliseconds; }
            }
        }
        catch (TimeoutException exception)
        {
            counters.Outcome = "Timeout";
            wait?.Fail(exception);
            return null;
        }
        catch (Exception exception)
        {
            counters.Outcome = exception is OperationCanceledException ? "Canceled" : "Failed";
            wait?.Fail(exception);
            throw;
        }
        finally
        {
            // Only numeric counters/fixed outcomes, never scripts, slot names,
            // returned page values, browser URLs, or exception messages.
            if (wait is not null) wait.ScriptWait = new(timeout.TotalMilliseconds, script is not null,
                counters.NativeCalls, counters.PollCalls, counters.NullResponses, counters.DispatchMs,
                counters.PollNativeMs, counters.DelayMs, counters.Outcome);
        }
    }

    private static async Task<string> ExecuteAsync(Func<string, Task<string>> execute, string expression,
        TimeSpan timeout, WaitCounters counters, bool initial, CancellationToken cancellationToken)
    {
        counters.NativeCalls++;
        if (!initial) counters.PollCalls++;
        long started = Stopwatch.GetTimestamp();
        try
        {
            // Timeout stops host waiting only, not Chromium. A synchronously
            // blocked native dispatch is visible through AsyncActionDispatch.
            return await DiagnosticLog.MeasureAsync(initial ? DiagnosticStage.HostScriptDispatch : DiagnosticStage.HostScriptPollCall,
                () => execute(expression).WaitAsync(timeout, cancellationToken), onlySlow: true).ConfigureAwait(true);
        }
        finally
        {
            double duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (initial) counters.DispatchMs += duration;
            else counters.PollNativeMs += duration;
        }
    }

    private sealed class WaitCounters
    {
        internal int NativeCalls;
        internal int PollCalls;
        internal int NullResponses;
        internal double DispatchMs;
        internal double PollNativeMs;
        internal double DelayMs;
        internal string Outcome = "Pending";
    }
}

internal sealed record DiagnosticScriptWaitSnapshot(double TimeoutMs, bool IncludesInitialDispatch, int NativeCalls,
    int PollCalls, int NullResponses, double InitialDispatchMs, double PollNativeMs, double PollDelayMs, string Outcome);
