using System.Runtime.InteropServices;
using System.Windows;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Writes persistent text to the Windows clipboard. Clipboard ownership is global,
/// so brief locks from other applications are retried without blocking the UI thread.
/// </summary>
public sealed class WindowsClipboardTextService : IClipboardTextService
{
    private const int MaximumAttempts = 4;
    private const int RetryDelayMilliseconds = 50;

    public async Task<bool> TrySetTextAsync(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException) when (attempt < MaximumAttempts)
            {
                await Task.Delay(RetryDelayMilliseconds).ConfigureAwait(true);
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        return false;
    }
}
