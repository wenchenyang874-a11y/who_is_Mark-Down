using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

[Collection("Diagnostic log static isolation")]
public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public async Task Operation_Success_RecordsPairedPhaseDurationAndProcessClock()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentRead, 7)) operation.Succeed();
        await log.StopAsync();
        JsonElement[] rows = directory.Read();
        Assert.Equal(3, rows.Length);
        Assert.True(rows[0].GetProperty("launch_to_logger_ms").GetDouble() >= 0);
        Assert.Equal("1.8.6", rows[0].GetProperty("version").GetString());
        Assert.Equal("Started", rows[1].GetProperty("outcome").GetString());
        Assert.Equal("Succeeded", rows[2].GetProperty("outcome").GetString());
        Assert.Equal(rows[1].GetProperty("operation").GetInt64(), rows[2].GetProperty("operation").GetInt64());
        Assert.Equal(7, rows[2].GetProperty("window").GetInt32());
        Assert.True(rows[2].GetProperty("duration_ms").GetDouble() >= 0);
        Assert.True(rows[2].GetProperty("process_elapsed_ms").GetDouble() >= rows[2].GetProperty("log_elapsed_ms").GetDouble());
    }

    [Fact]
    public async Task Error_SecretInNestedException_RecordsTypesAndCodesWithoutMessagesOrPaths()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        const string secret = "SECRET_API_KEY_private_document";
        try { throw new IOException(secret + @" C:\private\note.md", new ArgumentException(secret)); }
        catch (IOException exception) { log.Error(DiagnosticStage.ImageUpload, exception); }
        await log.StopAsync();
        string text = string.Join("\n", Directory.GetFiles(directory.Path).Select(File.ReadAllText));
        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("private", text, StringComparison.Ordinal);
        JsonElement row = directory.Read()[1];
        Assert.Equal("System.IO.IOException", row.GetProperty("error_type").GetString());
        Assert.Equal("System.ArgumentException", row.GetProperty("inner_error_type").GetString());
        Assert.True(row.TryGetProperty("hresult", out _));
        Assert.True(row.TryGetProperty("frames", out _));
    }

    [Fact]
    public async Task Operation_UnconfirmedEarlyReturn_IsNotReportedAsSuccess()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using (log.Begin(DiagnosticStage.DocumentOpen)) { }
        await log.StopAsync();
        Assert.Equal("Abandoned", directory.Read()[2].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Operation_Canceled_RecordsCancellationNotFailure()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.PreviewRender)) operation.Fail(new OperationCanceledException());
        await log.StopAsync();
        Assert.Equal("Canceled", directory.Read()[2].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Operation_DisposedTwice_EmitsOnlyOneEnd()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentSave);
        operation.Succeed();
        operation.Dispose();
        operation.Dispose();
        await log.StopAsync();
        Assert.Equal(3, directory.Read().Length);
    }

    [Fact]
    public async Task SlowOnly_FastSuccess_SuppressesTypingNoiseButKeepsErrors()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.MarkdownParse, onlySlow: true)) operation.Succeed();
        using (DiagnosticOperation operation = log.Begin(DiagnosticStage.MarkdownParse, onlySlow: true)) operation.Fail(new IOException());
        await log.StopAsync();
        Assert.Equal(2, directory.Read().Length);
        Assert.Equal("Failed", directory.Read()[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Sink_PathIsFile_CompletesWithoutThrowingOrBlockingProducer()
    {
        using LogDirectory directory = new();
        string blocked = System.IO.Path.Combine(directory.Path, "blocked");
        await File.WriteAllTextAsync(blocked, "keep", TestContext.Current.CancellationToken);
        LocalDiagnosticLog log = new(blocked, "1.8.6");
        log.Mark(DiagnosticStage.ApplicationConstructor);
        await log.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(log.FailureType);
        Assert.Equal("keep", await File.ReadAllTextAsync(blocked, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Queue_Burst_StaysBoundedAndCountsDroppedRecords()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6", capacity: 1);
        for (int index = 0; index < 10000; index++) log.Mark(DiagnosticStage.WindowLoaded);
        await log.StopAsync();
        Assert.True(log.DroppedEntries > 0);
        Assert.Equal("LogOverflow", directory.Read()[^1].GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Stop_RepeatedAndLateEvents_CompletesAndRejectsLateWrites()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        log.Mark(DiagnosticStage.ApplicationExit);
        await log.StopAsync();
        log.Mark(DiagnosticStage.DocumentOpen);
        await log.StopAsync();
        Assert.Equal(2, directory.Read().Length);
    }

    [Fact]
    public async Task Rotation_SizeLimit_PrunesOnlyOwnedFilesAndPreservesOtherFiles()
    {
        using LogDirectory directory = new();
        string unrelated = System.IO.Path.Combine(directory.Path, "wimd-user-note.jsonl");
        await File.WriteAllTextAsync(unrelated, "keep", TestContext.Current.CancellationToken);
        LocalDiagnosticLog log = new(directory.Path, "1.8.6", capacity: 2048, maximumFileBytes: 4096, retainedFiles: 3);
        for (int index = 0; index < 400; index++) log.Mark(DiagnosticStage.DocumentRead);
        await log.StopAsync();
        string[] files = Directory.GetFiles(directory.Path).Where(path => path != unrelated).ToArray();
        Assert.InRange(files.Length, 2, 3);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 6000));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWriters_SeparateSessionFiles_DoNotInterleaveRecords()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog first = new(directory.Path, "first");
        LocalDiagnosticLog second = new(directory.Path, "second");
        first.Mark(DiagnosticStage.DocumentOpen, 1);
        second.Mark(DiagnosticStage.DocumentSave, 2);
        await Task.WhenAll(first.StopAsync(), second.StopAsync());
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        Assert.All(Directory.GetFiles(directory.Path), path => Assert.Equal(2, File.ReadAllLines(path).Length));
    }

    [Fact]
    public async Task Measure_ThrownException_PreservesOriginalAndLogsFailure()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        DiagnosticLog.Current = log;
        IOException expected = new("not logged");
        try
        {
            IOException actual = await Assert.ThrowsAsync<IOException>(() => DiagnosticLog.MeasureAsync(
                DiagnosticStage.DocumentRead, () => Task.FromException(expected)));
            Assert.Same(expected, actual);
        }
        finally { DiagnosticLog.Current = null; await log.StopAsync(); }
        Assert.Equal("Failed", directory.Read()[2].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NestedOperations_AcrossAwait_PreserveWindowAndParentCorrelation()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using (DiagnosticOperation parent = log.Begin(DiagnosticStage.DocumentOpen, 9))
        {
            await Task.Yield();
            using (DiagnosticOperation child = log.Begin(DiagnosticStage.DocumentRead)) child.Succeed();
            parent.Succeed();
        }
        await log.StopAsync();
        JsonElement[] rows = directory.Read();
        long parentId = rows[1].GetProperty("operation").GetInt64();
        Assert.Equal(parentId, rows[2].GetProperty("parent_operation").GetInt64());
        Assert.Equal(parentId, rows[3].GetProperty("parent_operation").GetInt64());
        Assert.Equal(9, rows[2].GetProperty("window").GetInt32());
        Assert.Equal(0, rows[4].GetProperty("parent_operation").GetInt64());
    }

    [Fact]
    public async Task PendingOperation_BeforeCompletion_StartIsAlreadyDurable()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog log = new(directory.Path, "1.8.6");
        using DiagnosticOperation operation = log.Begin(DiagnosticStage.DocumentRead);
        try
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (!directory.Read().Any(row => row.TryGetProperty("outcome", out JsonElement outcome) && outcome.GetString() == "Started"))
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5));
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.Contains(directory.Read(), row => row.TryGetProperty("outcome", out JsonElement outcome) && outcome.GetString() == "Started");
            operation.Cancel();
        }
        finally { operation.Dispose(); await log.StopAsync(); }
    }

    [Fact]
    public async Task Rotation_AnotherWriterStillActive_DoesNotDeleteItsLog()
    {
        using LogDirectory directory = new();
        LocalDiagnosticLog first = new(directory.Path, "first", retainedFiles: 1);
        first.Mark(DiagnosticStage.ApplicationConstructor);
        Stopwatch timeout = Stopwatch.StartNew();
        while (Directory.GetFiles(directory.Path).Length == 0)
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        LocalDiagnosticLog second = new(directory.Path, "second", retainedFiles: 1);
        second.Mark(DiagnosticStage.ApplicationConstructor);
        await second.StopAsync();
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        await first.StopAsync();
    }

    private sealed class LogDirectory : IDisposable
    {
        internal LogDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WIMD-log-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal JsonElement[] Read() => Directory.GetFiles(Path, "wimd-*.jsonl")
            .SelectMany(path =>
            {
                // Live readers must share the existing writer's write access;
                // File.ReadAllLines uses FileShare.Read and cannot tail a log.
                using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new(input);
                return reader.ReadToEnd().Split('\n').SkipLast(1);
            }).Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).ToArray();

        public void Dispose() => Directory.Delete(Path, true);
    }
}

[CollectionDefinition("Diagnostic log static isolation", DisableParallelization = true)]
public sealed class DiagnosticLogIsolation;
