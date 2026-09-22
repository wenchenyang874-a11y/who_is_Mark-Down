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

        await core.ExecuteScriptAsync(script).ConfigureAwait(true);
        return await PollAsync(core, slotExpression, timeout, cancellationToken)
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

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string result = await core.ExecuteScriptAsync(slotExpression).ConfigureAwait(true);
            string trimmed = result.Trim();
            if (!trimmed.Equals("null", StringComparison.Ordinal))
            {
                return trimmed;
            }

            await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(true);
        }

        return null;
    }
}
