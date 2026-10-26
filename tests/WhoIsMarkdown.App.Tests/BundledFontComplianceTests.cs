using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using WhoIsMarkdown.Core.Settings;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class BundledFontComplianceTests
{
    [Theory]
    [InlineData("wimd:lxgw-wenkai-mono", "895CE1D2FA00D5CA3BEC0329D38E3190454F4B5FA7B4733F491C419FAF9753A1")]
    [InlineData("wimd:lxgw-marker-gothic", "CFC0FACBC1084735596425F91650C0FFEF2130F0853556476BDD9F59E164D382")]
    [InlineData("wimd:lxgw-wenkai", "2D73EEA94AD808999D90C0000C239A84A33B09D4BC6AB6C5B8BBF1EDA8A7FC8B")]
    [InlineData("wimd:jetbrains-mono", "A0BF60EF0F83C5ED4D7A75D45838548B1F6873372DFAC88F71804491898D138F")]
    [InlineData("wimd:fira-code", "5992AB9640E2DF491B2F609467B1DE60E8BC39B2C28DB184342A0592D98F6117")]
    public void 字体合规_原始文件摘要与随附许可完整(string id, string hash)
    {
        BundledFont font = BundledFontCatalog.Find(id)!;
        string root = FindRoot();
        string path = Path.Combine(root, "assets", "fonts", font.RelativePath);
        using FileStream file = File.OpenRead(path);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(file)));
        string license = File.ReadAllText(Path.Combine(root, "assets", "fonts", font.LicenseDirectory, "OFL.txt"));
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", license, StringComparison.Ordinal);
        Assert.Contains("PERMISSION & CONDITIONS", license, StringComparison.Ordinal);
        Assert.Contains("DISCLAIMER", license, StringComparison.Ordinal);
        GlyphTypeface face = new(new Uri(path));
        Assert.Equal(FontEmbeddingRight.Installable, face.EmbeddingRights);
        Assert.Contains(font.FamilyName, face.FamilyNames.Values);
        if (font.IsChinese) Assert.True(face.CharacterToGlyphMap.ContainsKey('中'));
        string notice = File.ReadAllText(Path.Combine(root, "assets", "fonts", "README.md"));
        Assert.Contains(hash, notice, StringComparison.Ordinal);
        Assert.Contains("Copyright", notice, StringComparison.Ordinal);
        string licenseHash = font.LicenseDirectory switch
        {
            "lxgw-wenkai" or "lxgw-wenkai-mono" => "1A25E35DA1031C6C3436FDE545BB9CB5ACA954E9873AFE510C834B8B79BD21A0",
            "lxgw-marker-gothic" => "A5E70C8B156C4103B96A4F6AF65AD6562E03C0E87373DC3AB43E04D6997EC46E",
            "jetbrains-mono" => "30F0C136E3C88E422D0791ACD97238870F9054A9729BC34CF2FF0D4ED8CAC4AD",
            "fira-code" => "1D41E10031AB125302780A05EC4C91D218E47DB0C7E37CF315CCE5E608CDC25C",
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(licenseHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
            Path.Combine(root, "assets", "fonts", font.LicenseDirectory, "OFL.txt")))));
    }

    [Fact]
    public void 内置字体检索_中文分类与开源标签覆盖对应字体()
    {
        var options = AppearanceSettingsWindow.CreateBundledFontOptions();
        Assert.Equal(5, options.Count);
        Assert.All(options, option => Assert.Contains("开源", option.SearchText, StringComparison.Ordinal));
        Assert.Equal(3, options.Count(option => option.SearchText.Contains("中文", StringComparison.Ordinal)));
        Assert.All(options.Where(option => option.IsChineseFont), option =>
            Assert.Contains("Chinese CJK", option.SearchText, StringComparison.Ordinal));
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WhoIsMarkdown.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
}
