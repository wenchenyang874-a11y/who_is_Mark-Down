using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class Bgra32AlphaNormalizerTests
{
    [Fact]
    public void 修复透明通道_所有Alpha为零_恢复不透明且保留颜色()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            40, 50, 60, 0,
        ];

        bool repaired = Bgra32AlphaNormalizer.RestoreOpaqueAlphaWhenMissing(pixels);

        Assert.True(repaired);
        Assert.Equal(
            [
                10, 20, 30, 255,
                40, 50, 60, 255,
            ],
            pixels);
    }

    [Fact]
    public void 修复透明通道_存在非零Alpha_保持原始透明度不变()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            40, 50, 60, 128,
            70, 80, 90, 255,
        ];
        byte[] original = [.. pixels];

        bool repaired = Bgra32AlphaNormalizer.RestoreOpaqueAlphaWhenMissing(pixels);

        Assert.False(repaired);
        Assert.Equal(original, pixels);
    }

    [Fact]
    public void 修复透明通道_数据不是完整像素_拒绝处理()
    {
        byte[] pixels = [10, 20, 30];

        Assert.Throws<ArgumentException>(() =>
            Bgra32AlphaNormalizer.RestoreOpaqueAlphaWhenMissing(pixels));
    }
}
