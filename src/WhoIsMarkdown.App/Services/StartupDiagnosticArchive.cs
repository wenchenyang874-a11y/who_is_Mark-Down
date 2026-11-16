using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Startup evidence must survive days of ordinary-log rotation. This sink is
/// owned exclusively by the existing dedicated writer thread, never by the UI.
/// It copies already-redacted records, bounds both bytes and capture time, and
/// keeps summaries independently from detailed startup traces and ordinary logs.
/// </summary>
internal sealed class StartupDiagnosticArchive : IDisposable
{
    internal const int DetailLimit = 2 * 1024 * 1024;
    internal const int SummaryLimit = 128 * 1024;
    private readonly FileStream detail;
    private readonly FileStream summary;
    private readonly HashSet<string> summaryKeys = [];
    internal const double CaptureMilliseconds = 120_000;
    private long skippedDetail;
    private long skippedSummary;
    private long deduplicatedSummary;
    private bool finished;

    internal StartupDiagnosticArchive(string logDirectory, string name, Action<FileStream> writeHeader)
    {
        string directory = Path.Combine(logDirectory, "Startup");
        string summaries = Path.Combine(directory, "Summaries");
        Directory.CreateDirectory(summaries);
        detail = Open(directory, name);
        try
        {
            summary = Open(summaries, name);
            try { writeHeader(detail); writeHeader(summary); }
            catch { summary.Dispose(); throw; }
        }
        catch { detail.Dispose(); throw; }
        try { Trim(directory, 60); Trim(summaries, 200); }
        catch { try { detail.Dispose(); } finally { summary.Dispose(); } throw; }
    }

    internal bool IsFinished => finished;

    internal void Append(byte[] payload, string stage, string outcome, int window, double eventElapsedMs)
    {
        if (finished) return;
        if (eventElapsedMs > CaptureMilliseconds) { Finish("CaptureDeadline"); return; }
        // Keep a fixed window, not "first window ready + N seconds": another
        // restored window may still be starting. Daily noise cannot evict this
        // file and summaries have their own quota even if detail fills early.
        // Leave space for an explicit footer even when a noisy startup fills its
        // quota. Losing details never silently masquerades as full coverage.
        if (detail.Position + payload.Length <= DetailLimit - 1024) detail.Write(payload);
        else
        {
            // Persist the first loss immediately, not only on clean shutdown.
            // A killed/hung process may never reach the footer.
            if (skippedDetail++ == 0)
                detail.Write(Encoding.UTF8.GetBytes("{\"stage\":\"StartupArchiveDetailLimit\",\"detail_truncated\":true}\n"));
        }
        if (IsSummaryStage(stage))
        {
            string key = $"{window}:{stage}:{outcome}";
            if (summaryKeys.Contains(key)) { deduplicatedSummary++; return; }
            if (summaryKeys.Count < 256 && summary.Position + payload.Length <= SummaryLimit - 1024)
            {
                summaryKeys.Add(key);
                summary.Write(payload);
            }
            else skippedSummary++;
        }
    }

    internal void Tick(double elapsedMs)
    {
        if (!finished && elapsedMs >= CaptureMilliseconds) Finish("CaptureDeadline");
    }

    internal void Flush()
    {
        if (finished) return;
        detail.Flush();
        summary.Flush();
    }

    internal void Finish(string reason)
    {
        if (finished) return;
        finished = true;
        try
        {
            byte[] footer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                stage = "StartupArchiveEnd",
                reason,
                capture_deadline_ms = CaptureMilliseconds,
                skipped_detail_records = skippedDetail,
                skipped_summary_records = skippedSummary,
                deduplicated_summary_records = deduplicatedSummary,
                detail_limit_bytes = DetailLimit,
                summary_limit_bytes = SummaryLimit,
            }) + "\n");
            detail.Write(footer);
            summary.Write(footer);
        }
        finally { try { detail.Dispose(); } finally { summary.Dispose(); } }
    }

    public void Dispose() => Finish("LoggerStopped");

    private static bool IsSummaryStage(string stage)
        => stage.StartsWith("Application", StringComparison.Ordinal)
            || stage.StartsWith("Startup", StringComparison.Ordinal)
            || stage.StartsWith("Window", StringComparison.Ordinal)
            || stage.StartsWith("Wpf", StringComparison.Ordinal)
            || stage.StartsWith("WebView", StringComparison.Ordinal)
            || stage is "ManagedEntry" or "PreviewInitialize" or "PreviewRendererPrepare"
                or "WebViewEnvironment" or "WebViewControl" or "DocumentOpen" or "DocumentRead"
                or "SettingsLoad" or "SettingsApply" or "ManagedStackCapture" or "DiagnosticCoverage" or "LogOverflow";

    private static FileStream Open(string directory, string name)
        => new(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, FileOptions.SequentialScan);

    private static void Trim(string directory, int keep)
    {
        // Never recurse or delete other logs/ETL/user files. An active archive is
        // opened without Delete sharing and is retained even above the count cap.
        foreach (FileInfo file in new DirectoryInfo(directory).GetFiles("startup-*.jsonl")
            .Where(file => IsOwned(file.Name)).OrderByDescending(file => file.CreationTimeUtc).Skip(keep))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsOwned(string name)
    {
        string[] parts = Path.GetFileNameWithoutExtension(name).Split('-');
        return parts.Length == 4 && parts[0] == "startup"
            && DateTime.TryParseExact(parts[1], "yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && Guid.TryParseExact(parts[3], "N", out _);
    }
}
