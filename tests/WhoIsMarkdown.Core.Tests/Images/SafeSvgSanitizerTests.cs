using System.Text;
using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.Core.Tests.Images;

public sealed class SafeSvgSanitizerTests
{
    [Fact]
    public void 安全过滤_活动内容与外部资源_移除且保留静态图形()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 80 40" onload="alert(1)">
              <style>.safe { fill: url(#paint); }</style>
              <defs><linearGradient id="paint"><stop offset="0" stop-color="#fff" /></linearGradient></defs>
              <script>alert(1)</script>
              <animate attributeName="x" />
              <image href="https://tracker.example/pixel.png" />
              <use href="https://tracker.example/icon.svg#id" />
              <circle r="10" fill="url(https://tracker.example/paint.svg#id)" />
              <rect class="safe" width="80" height="40" onclick="alert(2)" />
            </svg>
            """;

        SafeSvgSanitizationResult result = SafeSvgSanitizer.Sanitize(
            Encoding.UTF8.GetBytes(source));
        string safe = Encoding.UTF8.GetString(result.Bytes);

        Assert.DoesNotContain("script", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("animate", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("image", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<rect", safe, StringComparison.Ordinal);
        Assert.Contains("url(#paint)", safe, StringComparison.Ordinal);
        Assert.True(result.RemovedElementCount >= 3);
        Assert.True(result.RemovedAttributeCount >= 2);
    }

    [Fact]
    public void 安全过滤_文档类型定义_拒绝解析()
    {
        const string source = """
            <!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <svg xmlns="http://www.w3.org/2000/svg"><text>&xxe;</text></svg>
            """;

        SafeSvgException exception = Assert.Throws<SafeSvgException>(() =>
            SafeSvgSanitizer.Sanitize(Encoding.UTF8.GetBytes(source)));

        Assert.Contains("安全静态解析", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 安全过滤_非Svg根元素_拒绝解析()
    {
        byte[] source = Encoding.UTF8.GetBytes("<html><body>not svg</body></html>");

        SafeSvgException exception = Assert.Throws<SafeSvgException>(() =>
            SafeSvgSanitizer.Sanitize(source));

        Assert.Contains("不是有效", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 安全过滤_内部引用与Xml语言属性_保留()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg"
                 xmlns:xlink="http://www.w3.org/1999/xlink"
                 xml:lang="zh-CN"
                 viewBox="0 0 40 40">
              <defs><path id="mark" d="M0 0L40 40" /></defs>
              <use xlink:href="#mark" stroke="#333" />
            </svg>
            """;

        SafeSvgSanitizationResult result = SafeSvgSanitizer.Sanitize(
            Encoding.UTF8.GetBytes(source));
        string safe = Encoding.UTF8.GetString(result.Bytes);

        Assert.Contains("xml:lang=\"zh-CN\"", safe, StringComparison.Ordinal);
        Assert.Contains("xlink:href=\"#mark\"", safe, StringComparison.Ordinal);
        Assert.Equal(0, result.RemovedElementCount);
        Assert.Equal(0, result.RemovedAttributeCount);
    }

    [Fact]
    public void 安全过滤_危险样式与未知命名空间_移除()
    {
        const string source = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:bad="urn:bad">
              <!-- discard -->
              <?wimd discard?>
              <style>@import url(https://tracker.example/theme.css);</style>
              <bad:widget bad:value="x" />
              <rect width="20" height="20" data-private="x"
                    style="fill: url(https://tracker.example/paint.svg#id)" />
            </svg>
            """;

        SafeSvgSanitizationResult result = SafeSvgSanitizer.Sanitize(
            Encoding.UTF8.GetBytes(source));
        string safe = Encoding.UTF8.GetString(result.Bytes);

        Assert.DoesNotContain("tracker.example", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("bad:widget", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("data-private", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("discard", safe, StringComparison.Ordinal);
        Assert.Contains("<rect", safe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 安全过滤_文件缺失或为空_拒绝读取()
    {
        using TemporaryDirectory temporary = new();
        string missingPath = Path.Combine(temporary.Path, "missing.svg");
        string emptyPath = Path.Combine(temporary.Path, "empty.svg");
        await File.WriteAllBytesAsync(
            emptyPath,
            [],
            TestContext.Current.CancellationToken);

        SafeSvgException missing = await Assert.ThrowsAsync<SafeSvgException>(() =>
            SafeSvgSanitizer.SanitizeFileAsync(
                missingPath,
                TestContext.Current.CancellationToken));
        SafeSvgException empty = await Assert.ThrowsAsync<SafeSvgException>(() =>
            SafeSvgSanitizer.SanitizeFileAsync(
                emptyPath,
                TestContext.Current.CancellationToken));

        Assert.Contains("不存在", missing.Message, StringComparison.Ordinal);
        Assert.Contains("为空", empty.Message, StringComparison.Ordinal);
    }
}
