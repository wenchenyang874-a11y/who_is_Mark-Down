using System.Diagnostics;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class CurrentProcessCountersTests
{
    [Fact]
    public void Read_CurrentProcess_MatchesCpuRangeAndWorkingSetScale()
    {
        using Process process = Process.GetCurrentProcess();
        TimeSpan before = process.TotalProcessorTime;
        (TimeSpan cpu, long memory) = CurrentProcessCounters.Read();
        TimeSpan after = process.TotalProcessorTime;
        Assert.InRange(cpu, before, after);
        Assert.True(memory > 0);
        // Working sets can change between calls, particularly in a parallel test
        // runner. Check ABI/units without pretending these are atomic snapshots.
        Assert.InRange((double)memory / process.WorkingSet64, 0.25, 4);
    }

    [Fact]
    public void Read_RepeatedQueries_KeepCpuMonotonicAndDoNotOwnHandles()
    {
        using Process process = Process.GetCurrentProcess();
        CurrentProcessCounters.Read();
        int handles = process.HandleCount;
        TimeSpan previous = TimeSpan.Zero;
        for (int i = 0; i < 1000; i++)
        {
            (TimeSpan cpu, long memory) = CurrentProcessCounters.Read();
            Assert.True(cpu >= previous);
            Assert.True(memory > 0);
            previous = cpu;
        }
        process.Refresh();
        // Other xUnit workers may open handles, but one per read would be obvious.
        Assert.True(process.HandleCount - handles < 500);
    }
}
