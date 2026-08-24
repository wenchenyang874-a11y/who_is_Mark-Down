using WhoIsMarkdown.Core.Diagnostics;

namespace WhoIsMarkdown.Core.Tests.Diagnostics;

public sealed class ProcessResourceUsageCalculatorTests
{
    [Fact]
    public void 资源占用_按逻辑处理器归一化Cpu并保留工作集()
    {
        ProcessResourceUsage usage = ProcessResourceUsageCalculator.Calculate(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(1),
            logicalProcessorCount: 4,
            workingSetBytes: 128 * 1024 * 1024);

        Assert.Equal(50d, usage.CpuPercentage, precision: 6);
        Assert.Equal(128 * 1024 * 1024, usage.WorkingSetBytes);
    }

    [Fact]
    public void 资源占用_异常时间差和负内存_返回安全下限()
    {
        ProcessResourceUsage usage = ProcessResourceUsageCalculator.Calculate(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(4),
            TimeSpan.Zero,
            logicalProcessorCount: 8,
            workingSetBytes: -1);

        Assert.Equal(0d, usage.CpuPercentage);
        Assert.Equal(0, usage.WorkingSetBytes);
    }

    [Fact]
    public void 资源占用_采样抖动导致结果超限_限制为百分之百()
    {
        ProcessResourceUsage usage = ProcessResourceUsageCalculator.Calculate(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(1),
            logicalProcessorCount: 4,
            workingSetBytes: 1);

        Assert.Equal(100d, usage.CpuPercentage);
    }

    [Fact]
    public void 资源占用_处理器数量无效_拒绝计算()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcessResourceUsageCalculator.Calculate(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                logicalProcessorCount: 0,
                workingSetBytes: 0));
    }
}
