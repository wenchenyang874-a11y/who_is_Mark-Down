using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Diagnostics;

namespace WhoIsMarkdown.App;

/// <summary>
/// Samples the current WIMD process for the status bar. Windows exposes
/// resource counters per process rather than per WPF Window, so the display
/// deliberately describes the process that owns this main window.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan PerformanceRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly DispatcherTimer performanceTimer = new(DispatcherPriority.Background)
    {
        Interval = PerformanceRefreshInterval,
    };

    private bool performanceMonitorActive;
    private TimeSpan previousProcessorTime;
    private long previousPerformanceTimestamp;
    private bool hasPreviousPerformanceSample;
    private long lastDiagnosticPerformanceTimestamp;
    private DiagnosticDispatcherWatch? diagnosticDispatcherWatch;

    private void InitializePerformanceMonitor()
    {
        diagnosticDispatcherWatch = DiagnosticLog.Current?.WatchDispatcher(Dispatcher, diagnosticWindowId);
        performanceMonitorActive = true;
        performanceTimer.Tick += PerformanceTimer_Tick;
        UpdatePerformanceDisplay();
        performanceTimer.Start();
    }

    private void PerformanceTimer_Tick(object? sender, EventArgs eventArgs)
    {
        UpdatePerformanceDisplay();
    }

    private void UpdatePerformanceDisplay()
    {
        if (!performanceMonitorActive || windowClosed)
        {
            return;
        }

        try
        {
            long timestamp = Stopwatch.GetTimestamp();
            // Never enumerate system processes on the UI thread. Keep a quiet
            // diagnostic scope so an unusually slow native query is still visible.
            (TimeSpan processorTime, long workingSetBytes) = ReadPerformanceCounters();
            if (lastDiagnosticPerformanceTimestamp == 0
                || Stopwatch.GetElapsedTime(lastDiagnosticPerformanceTimestamp, timestamp) >= TimeSpan.FromSeconds(60))
            {
                // Reuse existing counters once per minute; no extra process scan
                // or timer. These are main-process metrics, not per-window or
                // Chromium memory. A sampling gap can also mean sleep/debugging.
                DiagnosticLog.Current?.Mark(DiagnosticStage.ProcessWorkingSetBytes, diagnosticWindowId, workingSetBytes);
                DiagnosticLog.Current?.Mark(DiagnosticStage.ProcessCpuTotalMilliseconds, diagnosticWindowId, (long)processorTime.TotalMilliseconds);
                lastDiagnosticPerformanceTimestamp = timestamp;
            }

            if (hasPreviousPerformanceSample)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(previousPerformanceTimestamp, timestamp);
                if (elapsed >= TimeSpan.FromSeconds(3))
                    DiagnosticLog.Current?.Mark(DiagnosticStage.DispatcherSampleGap, diagnosticWindowId, (long)elapsed.TotalMilliseconds);
                ProcessResourceUsage usage = ProcessResourceUsageCalculator.Calculate(
                    previousProcessorTime,
                    processorTime,
                    elapsed,
                    Environment.ProcessorCount,
                    workingSetBytes);
                PerformanceText.Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"CPU {usage.CpuPercentage:0.0}%  ·  内存 {FormatMemory(usage.WorkingSetBytes)}");
            }
            else
            {
                PerformanceText.Text = $"CPU 0.0%  ·  内存 {FormatMemory(workingSetBytes)}";
            }

            previousPerformanceTimestamp = timestamp;
            previousProcessorTime = processorTime;
            hasPreviousPerformanceSample = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // A transient counter failure must never affect editing or shutdown.
            hasPreviousPerformanceSample = false;
            PerformanceText.Text = "CPU --  ·  内存 --";
        }
    }

    private (TimeSpan ProcessorTime, long WorkingSetBytes) ReadPerformanceCounters()
    {
        using DiagnosticOperation? sample = DiagnosticLog.Current?.Begin(
            DiagnosticStage.ProcessCounterRead, diagnosticWindowId, onlySlow: true,
            execution: DiagnosticExecutionKind.Synchronous);
        try
        {
            var counters = CurrentProcessCounters.Read();
            sample?.Succeed();
            return counters;
        }
        catch (Exception exception) { sample?.Fail(exception); throw; }
    }

    private static string FormatMemory(long bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024d;
        double megabytes = Math.Max(0, bytes) / bytesPerMegabyte;
        return megabytes >= 1024d
            ? $"{megabytes / 1024d:0.00} GB"
            : $"{megabytes:0.0} MB";
    }

    private void DisposePerformanceMonitor()
    {
        diagnosticDispatcherWatch?.Dispose();
        diagnosticDispatcherWatch = null;
        performanceTimer.Stop();
        performanceTimer.Tick -= PerformanceTimer_Tick;
        performanceMonitorActive = false;
    }
}
