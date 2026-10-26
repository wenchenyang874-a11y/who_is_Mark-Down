using System.Text.Json;

namespace WhoIsMarkdown.App.Services;

public sealed partial class PreviewWebViewService
{
    /// <summary>
    /// A body patch can switch documents without navigation. Compare opaque editor
    /// session identities (never window/document objects) before the same native
    /// mutation, so matching heading labels in another file cannot inherit folds.
    /// Legacy callers without a session identity retain lexical path isolation.
    /// </summary>
    private string GetHeadingFoldResetScript(PreviewSnapshot snapshot)
    {
        if (committedPreview is null) return string.Empty;
        bool sameSession = snapshot.DocumentIdentity is not null || committedPreview.DocumentIdentity is not null
            ? ReferenceEquals(snapshot.DocumentIdentity, committedPreview.DocumentIdentity)
            : string.Equals(snapshot.DocumentPath, committedPreview.DocumentPath, StringComparison.OrdinalIgnoreCase);
        return sameSession ? string.Empty : "window.wimdHeadingFold?.reset();\n";
    }

    private void RecordHeadingFold(JsonElement root)
    {
        if (!TryReadHeadingFoldReport(root, out double milliseconds, out int count, out bool userAction)) return;
        DiagnosticLog.Current?.Record(userAction ? DiagnosticStage.PreviewHeadingFold : DiagnosticStage.PreviewHeadingFoldRefresh,
            DiagnosticOutcome.Succeeded, DiagnosticWindowId, duration: milliseconds, value: count);
    }

    internal static bool TryReadHeadingFoldReport(JsonElement root, out double milliseconds, out int count, out bool userAction)
    {
        milliseconds = 0;
        count = 0;
        userAction = false;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("elapsedMs", out JsonElement duration) || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetDouble(out milliseconds)
            || !double.IsFinite(milliseconds) || milliseconds is < 0 or > 60000
            || !root.TryGetProperty("collapsedCount", out JsonElement collapsed) || collapsed.ValueKind != JsonValueKind.Number
            || !collapsed.TryGetInt32(out count)
            || count is < 0 or > 1000000
            || !root.TryGetProperty("userAction", out JsonElement action) || action.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        userAction = action.GetBoolean();
        return true;
    }
}
