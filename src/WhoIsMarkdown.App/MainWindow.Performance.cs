using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
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

    private Process? performanceProcess;
    private TimeSpan previousProcessorTime;
    private long previousPerformanceTimestamp;
    private bool hasPreviousPerformanceSample;

    private void InitializePerformanceMonitor()
    {
        performanceProcess = Process.GetCurrentProcess();
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
        Process? process = performanceProcess;
        if (process is null || windowClosed)
        {
            return;
        }

        try
        {
            process.Refresh();
            long timestamp = Stopwatch.GetTimestamp();
            TimeSpan processorTime = process.TotalProcessorTime;
            long workingSetBytes = process.WorkingSet64;

            if (hasPreviousPerformanceSample)
            {
                TimeSpan elapsed = Stopwatch.GetElapsedTime(previousPerformanceTimestamp, timestamp);
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
        performanceTimer.Stop();
        performanceTimer.Tick -= PerformanceTimer_Tick;
        performanceProcess?.Dispose();
        performanceProcess = null;
    }
}
