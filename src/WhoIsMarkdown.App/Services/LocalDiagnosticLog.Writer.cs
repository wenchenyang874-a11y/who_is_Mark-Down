using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WhoIsMarkdown.App.Services;

// Bug fix: evidence of thread-pool starvation used to wait for that same pool.
// This entire writer is synchronous on a dedicated background thread, including
// file creation/rotation/flush. Producers remain bounded and never wait for disk.
internal sealed partial class LocalDiagnosticLog
{
    private void WriteLoop()
    {
        FileStream? output = null;
        try
        {
            using Process process = Process.GetCurrentProcess();
            DateTime processStarted = process.StartTime.ToUniversalTime();
            double launchToLoggerMs = Math.Max(0, (startedUtc - processStarted).TotalMilliseconds);
            Directory.CreateDirectory(DirectoryPath);
            int segment = 0;
            long reportedDropped = 0, lastOverflow = 0;
            while (true)
            {
                int written = 0;
                while (written < 64 && queue.Reader.TryRead(out DiagnosticEntry? entry))
                {
                    output = EnsureOutput(output, ref segment, launchToLoggerMs, processStarted);
                    WriteEntry(output, entry, launchToLoggerMs);
                    written++;
                }
                bool finishing = queue.Reader.Completion.IsCompleted;
                foreach (DiagnosticCoverageSnapshot snapshot in coverage.TakeDue(finishing))
                {
                    output = EnsureOutput(output, ref segment, launchToLoggerMs, processStarted);
                    WriteLine(output, new
                    {
                        utc = DateTimeOffset.UtcNow,
                        log_elapsed_ms = Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                        process_elapsed_ms = launchToLoggerMs + Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                        stage = "DiagnosticCoverage",
                        outcome = "Point",
                        coverage = snapshot
                    });
                    written++;
                }
                long totalDropped = DroppedEntries;
                if ((totalDropped > reportedDropped && (reportedDropped == 0 || lastOverflow == 0
                    || Stopwatch.GetElapsedTime(lastOverflow).TotalSeconds >= 1)) || (finishing && totalDropped > 0))
                {
                    output = EnsureOutput(output, ref segment, launchToLoggerMs, processStarted);
                    WriteLine(output, new
                    {
                        utc = DateTimeOffset.UtcNow,
                        log_elapsed_ms = Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                        process_elapsed_ms = launchToLoggerMs + Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                        stage = "LogOverflow",
                        dropped = totalDropped,
                        dropped_since_last_report = totalDropped - reportedDropped,
                        final = finishing
                    });
                    reportedDropped = totalDropped;
                    lastOverflow = Stopwatch.GetTimestamp();
                    written++;
                }
                // A continuously busy producer must not postpone persistence
                // forever. Flush at most 64 normal records plus health summaries.
                if (written > 0) output?.Flush();
                if (finishing) break;
                if (queue.Reader.Count > 0) continue;
                // First rejection wakes us. Unreported aggregate counters need
                // a bounded timer, but a genuinely idle writer never polls.
                writerReady.WaitOne(coverage.HasPending || totalDropped > reportedDropped ? 1000 : Timeout.Infinite);
            }
        }
        catch (Exception exception)
        {
            // A dedicated Thread must never surface an unhandled diagnostic
            // exception that would terminate the otherwise healthy editor.
            FailureType = exception.GetType().Name;
            Interlocked.Exchange(ref stopped, 1);
            StopObservers();
            queue.Writer.TryComplete();
        }
        finally
        {
            if (output is not null)
            {
                try { output.Dispose(); }
                catch (IOException exception) { FailureType = exception.GetType().Name; }
            }
            writerReady.Dispose();
            writerCompleted.TrySetResult();
        }
    }

    private FileStream EnsureOutput(FileStream? output, ref int segment, double launchToLoggerMs, DateTime processStarted)
    {
        if (output is not null && output.Length < maximumFileBytes) return output;
        output?.Dispose();
        string name = string.Create(CultureInfo.InvariantCulture,
            $"wimd-{startedUtc:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{session}-{segment++:D4}.jsonl");
        FileStream created = new(Path.Combine(DirectoryPath, name), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read, 16384, FileOptions.SequentialScan);
        try { WriteHeader(created, launchToLoggerMs, processStarted); TrimOldFiles(); return created; }
        catch { created.Dispose(); throw; }
    }

    private void WriteHeader(FileStream output, double launchToLoggerMs, DateTime processStarted)
        => WriteLine(output, new
        {
            schema = 1,
            image_pipeline_revision = 2,
            diagnostic_revision = 8,
            settings_fallback_values = new { missing = 1, invalid_json = 2, invalid_utf8 = 3, null_json = 4 },
            async_completion_timing = "Task.Status first observed complete, not native IO completion; observation-to-resume is a lower bound",
            async_task_poll_ms = 250,
            writer_thread_pool = Thread.CurrentThread.IsThreadPoolThread,
            writer_native_thread_id = DiagnosticThreadProbe.NativeThreadId,
            writer_batch_records = 64,
            coverage_summary_interval_ms = 1000,
            coverage_counts = "rejected observation/capture attempts, not distinct operations; last context only",
            kernel_stacks_in_json = false,
            xaml_checkpoint_values = Enum.GetValues<XamlConstructionCheckpoint>().ToDictionary(value => (int)value, value => value.ToString()),
            slow_sample_thread_role = "Synchronous=execution_thread; AsyncAwait=initiating_thread_not_IO_worker; Unspecified=entry_only",
            dispatcher_heartbeat_ms = 1000,
            dispatcher_unresponsive_ms = 1500,
            managed_stack_trigger_ms = 2000,
            managed_stack_capture_ms = 2000,
            managed_stack_cooldown_ms = 60000,
            slow_probe_interval_ms = 1000,
            slow_probe_fast_samples_per_operation = 20,
            slow_probe_backoff_ms = 30000,
            slow_probe_semantics = "sampled OS state; CPU covers entry-thread activity including other work; OriginFrames are scope-entry frames, not a suspended-thread stack",
            session,
            pid = Environment.ProcessId,
            version,
            started_utc = startedUtc,
            launch_to_logger_ms = launchToLoggerMs,
            managed_entry_utc = ManagedEntryUtc,
            process_to_managed_ms = ManagedEntryUtc is { } managed ? Math.Max(0, (managed - processStarted).TotalMilliseconds) : (double?)null,
            runtime = Environment.Version.ToString(),
            processors = Environment.ProcessorCount,
            timing = "process_elapsed_ms includes pre-managed launch; duration_ms measures host waits, not OS I/O termination",
        });

    private void WriteEntry(FileStream output, DiagnosticEntry entry, double launchToLoggerMs)
        => WriteLine(output, new
        {
            utc = entry.Utc,
            writer_utc = DateTimeOffset.UtcNow,
            writer_queue_delay_ms = Math.Max(0, Stopwatch.GetElapsedTime(origin).TotalMilliseconds - entry.ElapsedMs),
            process_elapsed_ms = launchToLoggerMs + entry.ElapsedMs,
            log_elapsed_ms = entry.ElapsedMs,
            stage = entry.Stage,
            outcome = entry.Outcome,
            window = entry.Window,
            operation = entry.Operation,
            parent_operation = entry.ParentOperation,
            duration_ms = entry.DurationMs,
            jit_ms = entry.JitMilliseconds,
            compiled_methods = entry.CompiledMethods,
            managed_thread_id = entry.ManagedThreadId,
            native_thread_id = entry.NativeThreadId,
            owner_native_thread_id = entry.OwnerThread,
            owner_cpu_ms = entry.OwnerCpuMs,
            observation = entry.Observation,
            assembly = entry.Assembly,
            execution_kind = entry.Execution,
            sampled_stack = entry.Stack,
            async_timeline = entry.AwaitState,
            script_wait = entry.ScriptWait,
            value = entry.Value,
            error_type = entry.ErrorType,
            hresult = entry.HResult,
            inner_error_type = entry.InnerErrorType,
            inner_hresult = entry.InnerHResult,
            frames = entry.Frames,
        });

    private static void WriteLine<T>(FileStream output, T entry)
        => output.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n"));

    private void TrimOldFiles()
    {
        // Locked files belong to other active writers; exact names only.
        FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles("wimd-*.jsonl")
            .Where(file => IsOwnedFileName(file.Name)).OrderByDescending(file => file.CreationTimeUtc).ToArray();
        foreach (FileInfo file in files.Skip(retainedFiles))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsOwnedFileName(string name)
    {
        string[] parts = Path.GetFileNameWithoutExtension(name).Split('-');
        return parts.Length == 5 && parts[0] == "wimd"
            && DateTime.TryParseExact(parts[1], "yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && Guid.TryParseExact(parts[3], "N", out _) && parts[4].Length == 4
            && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
