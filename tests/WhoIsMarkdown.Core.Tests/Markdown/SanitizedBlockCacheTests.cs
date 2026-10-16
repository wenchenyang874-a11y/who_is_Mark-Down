using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Tests.Markdown;

public sealed class SanitizedBlockCacheTests
{
    [Fact]
    public void 缓存_重复块与淘汰_只对未命中执行过滤()
    {
        SanitizedBlockCache cache = new(maximumEntries: 2);
        int calls = 0;
        string Sanitize(string html) { calls++; return html.ToUpperInvariant(); }
        Assert.Equal("A", cache.GetOrAdd("a", Sanitize));
        cache.GetOrAdd("b", Sanitize);
        cache.GetOrAdd("a", Sanitize);
        cache.GetOrAdd("c", Sanitize);
        Assert.Equal(3, calls);
        cache.GetOrAdd("b", Sanitize);
        Assert.Equal(4, calls);
    }

    [Fact]
    public void 缓存_字符预算同时包含键和值_超大结果不驻留()
    {
        SanitizedBlockCache cache = new(maximumCharacters: 6);
        int calls = 0;
        string Sanitize(string html) { calls++; return html; }
        cache.GetOrAdd("aa", Sanitize);
        cache.GetOrAdd("bb", Sanitize);
        cache.GetOrAdd("aa", Sanitize);
        Assert.Equal(3, calls);
        cache.GetOrAdd("oversize", Sanitize);
        cache.GetOrAdd("oversize", Sanitize);
        Assert.Equal(5, calls);
        cache.GetOrAdd("aa", Sanitize);
        Assert.Equal(5, calls);
    }

    [Fact]
    public void 缓存_过滤失败_不得缓存不完整结果()
    {
        SanitizedBlockCache cache = new();
        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd("x", _ => throw new InvalidOperationException()));
        Assert.Equal("safe", cache.GetOrAdd("x", _ => "safe"));
    }
}
