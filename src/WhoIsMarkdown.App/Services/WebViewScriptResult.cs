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

        // Bound the native call too, not just the interval between polls. A closed
        // or unresponsive browser may never complete ExecuteScriptAsync. WaitAsync
        // stops our wait; it does not claim to interrupt Chromium's running script.
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero) return null;
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            await core.ExecuteScriptAsync(script).WaitAsync(timeout, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            return null;
        }

        return await PollAsync(core, slotExpression, timeout - elapsed.Elapsed, cancellationToken)
            .ConfigureAwait(true);
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

        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            string result;
            try
            {
                result = await core.ExecuteScriptAsync(slotExpression)
                    .WaitAsync(remaining, cancellationToken).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return null;
            }

            string trimmed = result.Trim();
            if (!trimmed.Equals("null", StringComparison.Ordinal))
            {
                return trimmed;
            }

            remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(PollIntervalMilliseconds, remaining.TotalMilliseconds)),
                cancellationToken).ConfigureAwait(true);
        }

        return null;
    }
}
