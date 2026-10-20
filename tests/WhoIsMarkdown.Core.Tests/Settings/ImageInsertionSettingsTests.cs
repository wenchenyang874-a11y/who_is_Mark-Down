using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class ImageInsertionSettingsTests
{
    [Fact]
    public void 规范化_默认设置_本地Img目录且信任全部远程图片()
    {
        ImageInsertionSettings result = new ImageInsertionSettings().Normalize();

        Assert.Equal(ImageStorageMode.Local, result.StorageMode);
        Assert.Equal(LocalImageStorageService.DefaultRelativeDirectory, result.LocalDirectory);
        Assert.Equal(RemoteImageTrustMode.TrustAll, result.TrustMode);
        Assert.Empty(result.RemoteImageRules);
    }

    [Fact]
    public void 规范化_废弃远程规则不再影响图片或启动()
    {
        ImageInsertionSettings settings = new()
        {
            StorageMode = ImageStorageMode.ImgBb,
            LocalDirectory = "../outside",
            TrustMode = RemoteImageTrustMode.TrustAll,
            RemoteImageRules = ["regex:(broken"],
            ProtectedImgBbApiKey = " encrypted-value ",
        };

        ImageInsertionSettings result = settings.Normalize();

        Assert.Equal(ImageStorageMode.ImgBb, result.StorageMode);
        Assert.Equal(LocalImageStorageService.DefaultRelativeDirectory, result.LocalDirectory);
        Assert.Equal(RemoteImageTrustMode.TrustAll, result.TrustMode);
        Assert.Empty(result.RemoteImageRules);
        Assert.Equal("encrypted-value", result.ProtectedImgBbApiKey);
    }
}
