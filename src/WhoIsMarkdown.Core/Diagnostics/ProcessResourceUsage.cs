namespace WhoIsMarkdown.Core.Diagnostics;

/// <summary>
/// Represents resource usage for one operating-system process. CPU is
/// normalized across all logical processors so the displayed range matches
/// the 0% to 100% convention used by Windows Task Manager.
/// </summary>
public sealed record ProcessResourceUsage(double CpuPercentage, long WorkingSetBytes);

public static class ProcessResourceUsageCalculator
{
    public static ProcessResourceUsage Calculate(
        TimeSpan previousProcessorTime,
        TimeSpan currentProcessorTime,
        TimeSpan elapsedTime,
        int logicalProcessorCount,
        long workingSetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessorCount, 1);

        TimeSpan processorDelta = currentProcessorTime - previousProcessorTime;
        double cpuPercentage = elapsedTime > TimeSpan.Zero && processorDelta > TimeSpan.Zero
            ? processorDelta.TotalMilliseconds
                / elapsedTime.TotalMilliseconds
                / logicalProcessorCount
                * 100d
            : 0d;

        return new ProcessResourceUsage(
            Math.Clamp(cpuPercentage, 0d, 100d),
            Math.Max(0, workingSetBytes));
    }
}
