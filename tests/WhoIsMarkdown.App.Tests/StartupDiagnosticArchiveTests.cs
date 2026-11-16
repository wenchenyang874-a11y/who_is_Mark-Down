using System.IO;
using System.Text;
using System.Text.Json;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

[Collection("Diagnostic log static isolation")]
public sealed class StartupDiagnosticArchiveTests
{
    [Fact]
    public async Task Archive_OrdinaryRotation_DoesNotEvictStartupEvidence()
    {
        using Fixture fixture = new();
        LocalDiagnosticLog log = new(fixture.Path, "isolated", maximumFileBytes: 4096, retainedFiles: 1);
        log.Mark(DiagnosticStage.WindowFirstFrame, 1);
        for (int i = 0; i < 300; i++) log.Mark(DiagnosticStage.DocumentSave, 1, i);
        log.Mark(DiagnosticStage.StartupPreviewReady, 1);
        await log.StopAsync();
        Assert.Null(log.FailureType);
        Assert.Null(log.StartupArchiveFailureType);
        Assert.Single(Directory.GetFiles(fixture.Path, "wimd-*.jsonl"));
        string detail = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Path + "/Startup", "*.jsonl")));
        string summary = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Path + "/Startup/Summaries", "*.jsonl")));
        Assert.Contains("WindowFirstFrame", detail, StringComparison.Ordinal);
        Assert.Contains("WindowFirstFrame", summary, StringComparison.Ordinal);
        Assert.Contains("StartupPreviewReady", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DocumentSave", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archive_StillStarting_FlushesWithoutWaitingForReadyOrExit()
    {
        using Fixture fixture = new();
        LocalDiagnosticLog log = new(fixture.Path, "isolated");
        log.Mark(DiagnosticStage.WindowXaml, 1);
        try
        {
            bool persisted = false;
            for (int i = 0; i < 100; i++)
            {
                string directory = fixture.Path + "/Startup";
                string? file = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.jsonl").FirstOrDefault() : null;
                if (file is not null)
                {
                    using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using StreamReader reader = new(input);
                    persisted = reader.ReadToEnd().Contains("WindowXaml", StringComparison.Ordinal);
                    if (persisted) break;
                }
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.True(persisted);
            Assert.False(log.Completion.IsCompleted);
        }
        finally { await log.StopAsync(); }
    }

    [Fact]
    public async Task Archive_UnwritableArchiveDirectory_OrdinaryLoggerSurvives()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.Path + "/Startup", "owned fixture prevents directory creation");
        LocalDiagnosticLog log = new(fixture.Path, "isolated");
        log.Mark(DiagnosticStage.WindowFirstFrame, 1);
        log.Mark(DiagnosticStage.StartupPreviewReady, 1);
        await log.StopAsync();
        Assert.Null(log.FailureType);
        Assert.NotNull(log.StartupArchiveFailureType);
        string regular = File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Path, "wimd-*.jsonl")));
        Assert.Contains("StartupArchive", regular, StringComparison.Ordinal);
        Assert.Contains("StartupPreviewReady", regular, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Path, regular, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_SizeAndTimeBudgets_KeepFooterAndRejectLaterEntries()
    {
        using Fixture fixture = new();
        using StartupDiagnosticArchive archive = fixture.Create();
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { stage = "DocumentSave", padding = new string('a', 20000) }) + "\n");
        for (int i = 0; i < 150; i++) archive.Append(payload, "DocumentSave", "Point", 1, i);
        byte[] ready = Encoding.UTF8.GetBytes("{\"stage\":\"StartupPreviewReady\"}\n");
        archive.Append(ready, "StartupPreviewReady", "Point", 1, 1000);
        archive.Tick(11001);
        Assert.False(archive.IsFinished); // Another restored window can still start.
        archive.Tick(120001);
        Assert.True(archive.IsFinished);
        archive.Append(payload, "DocumentSave", "Point", 1, 130000);
        string file = Assert.Single(Directory.GetFiles(fixture.Path + "/Startup", "*.jsonl"));
        Assert.True(new FileInfo(file).Length <= StartupDiagnosticArchive.DetailLimit);
        Assert.Contains("StartupArchiveDetailLimit", File.ReadAllText(file), StringComparison.Ordinal);
        using JsonDocument end = JsonDocument.Parse(File.ReadAllLines(file)[^1]);
        Assert.True(end.RootElement.GetProperty("skipped_detail_records").GetInt32() > 0);
        Assert.Equal("CaptureDeadline", end.RootElement.GetProperty("reason").GetString());
        Assert.Contains("StartupPreviewReady", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Path + "/Startup/Summaries", "*.jsonl"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_SummaryQuotaAndRetention_AreIndependentAndBounded()
    {
        using Fixture fixture = new();
        using (StartupDiagnosticArchive archive = fixture.Create())
        {
            byte[] row = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { stage = "WindowXaml", padding = new string('x', 8000) }) + "\n");
            for (int i = 0; i < 50; i++) archive.Append(row, "WindowXaml", "Point", i, 1);
        }
        string summary = Assert.Single(Directory.GetFiles(fixture.Path + "/Startup/Summaries", "*.jsonl"));
        Assert.True(new FileInfo(summary).Length <= StartupDiagnosticArchive.SummaryLimit);
        using JsonDocument footer = JsonDocument.Parse(File.ReadAllLines(summary)[^1]);
        Assert.True(footer.RootElement.GetProperty("skipped_summary_records").GetInt32() > 0);
        for (int i = 0; i < 201; i++) { using StartupDiagnosticArchive archive = fixture.Create(); }
        Assert.Equal(60, Directory.GetFiles(fixture.Path + "/Startup", "*.jsonl").Length);
        Assert.Equal(200, Directory.GetFiles(fixture.Path + "/Startup/Summaries", "*.jsonl").Length);
    }

    [Fact]
    public void Archive_NoReady_ClosesAtHardDeadlineAndDoesNotDeleteForeignOrActiveFiles()
    {
        using Fixture fixture = new();
        using StartupDiagnosticArchive active = fixture.Create();
        string foreign = fixture.Path + "/Startup/startup-user-notes.jsonl";
        File.WriteAllText(foreign, "leave alone");
        for (int i = 0; i < 62; i++) { using StartupDiagnosticArchive item = fixture.Create(); }
        Assert.True(File.Exists(foreign));
        active.Tick(120001);
        Assert.True(active.IsFinished);
        Assert.InRange(Directory.GetFiles(fixture.Path + "/Startup", "*.jsonl").Length, 60, 62);
        Assert.Equal(63, Directory.GetFiles(fixture.Path + "/Startup/Summaries", "*.jsonl").Length);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WIMD-startup-archive-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Path);
        internal StartupDiagnosticArchive Create() => new(Path,
            $"startup-20261102T090000000-123-{Guid.NewGuid():N}.jsonl", stream => stream.Write(Encoding.UTF8.GetBytes("{\"schema\":1}\n")));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
