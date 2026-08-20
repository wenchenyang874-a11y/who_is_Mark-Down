using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class BackgroundAppearanceScaleTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0.5)]
    [InlineData(100, 1)]
    [InlineData(-20, 0)]
    [InlineData(130, 1)]
    public void 百分比转不透明度_任意输入_零隐藏且一百完全显示(
        double percentage,
        double expectedOpacity)
    {
        double result = BackgroundAppearanceScale.FromPercentage(percentage);

        Assert.Equal(expectedOpacity, result);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(1, 100)]
    [InlineData(-0.2, 0)]
    [InlineData(1.3, 100)]
    public void 不透明度转百分比_任意输入_保持同向并限制范围(
        double opacity,
        double expectedPercentage)
    {
        double result = BackgroundAppearanceScale.ToPercentage(opacity);

        Assert.Equal(expectedPercentage, result);
    }
}
