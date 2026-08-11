namespace WhoIsMarkdown.Core.Security;

/// <summary>
/// Grants exactly one navigation to a generated preview document. WebView2 may
/// represent NavigateToString as either about:blank or a data:text/html URI, so URI
/// scheme alone cannot distinguish trusted host content from a document link.
/// </summary>
public sealed class PreviewNavigationGate
{
    private bool generatedNavigationPending;

    public void BeginGeneratedNavigation()
    {
        generatedNavigationPending = true;
    }

    public bool TryAllowGeneratedNavigation(string rawUri)
    {
        bool wasPending = generatedNavigationPending;
        generatedNavigationPending = false;

        return wasPending && IsGeneratedPreviewUri(rawUri);
    }

    public void CancelGeneratedNavigation()
    {
        generatedNavigationPending = false;
    }

    private static bool IsGeneratedPreviewUri(string rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return false;
        }

        return rawUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || rawUri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }
}
