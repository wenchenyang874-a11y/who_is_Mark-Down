using System.Text.Json;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.Core.Tests.Settings;

public sealed class PreviewStyleFileSettingsTests
{
    private static readonly string First = Path.GetFullPath(Path.Combine("style-fixtures", "a", "same.md"));
    private static readonly string Second = Path.GetFullPath(Path.Combine("style-fixtures", "b", "same.md"));
    private static PreviewStyleOverrides Style(OrderedListStyle? style) => new() { OrderedList = style };

    [Fact]
    public void 文件样式_不同完整路径隔离且大小写不影响匹配()
    {
        PreviewStyleFileSettings files = new PreviewStyleFileSettings().WithOverride(First, Style(OrderedListStyle.Circle))
            .WithOverride(Second, Style(OrderedListStyle.Plain));
        Assert.Equal(OrderedListStyle.Circle, files.GetOverride(First.ToUpperInvariant()).OrderedList);
        Assert.Equal(OrderedListStyle.Plain, files.GetOverride(Second).OrderedList);
        Assert.Null(files.GetOverride(null).OrderedList);
        Assert.Null(files.GetOverride("relative.md").OrderedList);
        Assert.Empty(new PreviewStyleFileSettings().FileOverrides);
    }

    [Fact]
    public void 文件样式_跟随全局移除覆盖且立即解析新的默认()
    {
        PreviewStyleFileSettings files = new PreviewStyleFileSettings().WithOverride(First, Style(OrderedListStyle.Circle))
            .WithOverride(Second, Style(OrderedListStyle.Emphasis)).WithOverride(First, Style(null));
        Assert.Single(files.FileOverrides);
        Assert.Equal(OrderedListStyle.Emphasis, files.GetOverride(Second).OrderedList);
        Assert.Equal(OrderedListStyle.RoundedSquare, files.GetOverride(First).Resolve(new() { OrderedList = OrderedListStyle.RoundedSquare }).OrderedList);
        Assert.Same(files, files.WithOverride(First, Style(null)));
    }

    [Fact]
    public void 归一化_旧配置空值相对路径与无效样式均安全回退()
    {
        Assert.Empty(JsonSerializer.Deserialize<ApplicationSettings>("{}")!.Normalize().PreviewStyleFiles.FileOverrides);
        Assert.Empty(JsonSerializer.Deserialize<ApplicationSettings>("{\"PreviewStyleFiles\":null}")!.Normalize().PreviewStyleFiles.FileOverrides);
        PreviewStyleFileSettings files = new()
        {
            FileOverrides = new Dictionary<string, PreviewStyleOverrides>
            {
                ["relative.md"] = Style(OrderedListStyle.Circle),
                [First] = Style((OrderedListStyle)999),
                [Second] = null!,
            },
        };
        Assert.Empty(files.Normalize().FileOverrides);
        Assert.Empty(new PreviewStyleFileSettings { FileOverrides = null! }.Normalize().FileOverrides);
        Assert.Throws<ArgumentException>(() => files.WithOverride("relative.md", Style(OrderedListStyle.Circle)));
    }

    [Theory]
    [InlineData(OrderedListStyle.Plain)]
    [InlineData(OrderedListStyle.Emphasis)]
    [InlineData(OrderedListStyle.Circle)]
    [InlineData(OrderedListStyle.RoundedSquare)]
    public void 持久化_文件覆盖与全局默认分别保存(OrderedListStyle style)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore store = new(path);
        store.Save(new()
        {
            PreviewStyles = new() { OrderedList = OrderedListStyle.Emphasis },
            PreviewStyleFiles = new PreviewStyleFileSettings().WithOverride(First, Style(style))
        });
        ApplicationSettings read = new JsonApplicationSettingsStore(path).Load();
        Assert.Equal(style, read.PreviewStyleFiles.GetOverride(First).OrderedList);
        Assert.Equal(OrderedListStyle.Emphasis, read.PreviewStyles.OrderedList);
        Assert.DoesNotContain("IsEmpty", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void 合并_不同窗口的文件修改与全局修改均保留()
    {
        ApplicationSettings initial = new();
        ApplicationSettings first = initial with { PreviewStyleFiles = initial.PreviewStyleFiles.WithOverride(First, Style(OrderedListStyle.Circle)) };
        ApplicationSettings second = initial with
        {
            PreviewStyles = new() { OrderedList = OrderedListStyle.Emphasis },
            PreviewStyleFiles = initial.PreviewStyleFiles.WithOverride(Second, Style(OrderedListStyle.RoundedSquare))
        };
        ApplicationSettings result = ApplicationSettingsChanges.Merge(first, initial, second);
        Assert.Equal(OrderedListStyle.Circle, result.PreviewStyleFiles.GetOverride(First).OrderedList);
        Assert.Equal(OrderedListStyle.RoundedSquare, result.PreviewStyleFiles.GetOverride(Second).OrderedList);
        Assert.Equal(OrderedListStyle.Emphasis, result.PreviewStyles.OrderedList);
        ApplicationSettings unrelated = initial with { BackgroundOpacity = .66 };
        ApplicationSettings final = ApplicationSettingsChanges.Merge(result, initial, unrelated);
        Assert.Equal(2, final.PreviewStyleFiles.FileOverrides.Count);
        Assert.Equal(OrderedListStyle.Emphasis, final.PreviewStyles.OrderedList);
    }

    [Fact]
    public void 合并_同文件最后显式修改获胜且跟随默认不被旧窗口复活()
    {
        ApplicationSettings initial = new() { PreviewStyleFiles = new PreviewStyleFileSettings().WithOverride(First, Style(OrderedListStyle.Circle)) };
        ApplicationSettings disk = initial with { PreviewStyleFiles = initial.PreviewStyleFiles.WithOverride(First, Style(OrderedListStyle.RoundedSquare)) };
        ApplicationSettings clear = initial with { PreviewStyleFiles = initial.PreviewStyleFiles.WithOverride(First, Style(null)) };
        ApplicationSettings result = ApplicationSettingsChanges.Merge(disk, initial, clear);
        Assert.Empty(result.PreviewStyleFiles.FileOverrides);
        ApplicationSettings stale = ApplicationSettingsChanges.Merge(result, initial, initial with { BackgroundOpacity = .5 });
        Assert.Empty(stale.PreviewStyleFiles.FileOverrides);
    }

    [Fact]
    public void 双存储器事务_过期快照不覆盖另一文件或恢复已移除设置()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        JsonApplicationSettingsStore first = new(path), second = new(path);
        ApplicationSettings baseline = new();
        first.Save(baseline);
        ApplicationSettings a = baseline with { PreviewStyleFiles = baseline.PreviewStyleFiles.WithOverride(First, Style(OrderedListStyle.Circle)) };
        ApplicationSettings b = baseline with { PreviewStyleFiles = baseline.PreviewStyleFiles.WithOverride(Second, Style(OrderedListStyle.Plain)) };
        first.SaveChanges(baseline, a);
        second.SaveChanges(baseline, b);
        ApplicationSettings both = first.Load();
        Assert.Equal(OrderedListStyle.Circle, both.PreviewStyleFiles.GetOverride(First).OrderedList);
        Assert.Equal(OrderedListStyle.Plain, both.PreviewStyleFiles.GetOverride(Second).OrderedList);
        first.SaveChanges(both, both with { PreviewStyleFiles = both.PreviewStyleFiles.WithOverride(First, Style(null)) });
        second.SaveChanges(b, b with { BackgroundOpacity = .5 });
        Assert.Null(first.Load().PreviewStyleFiles.GetOverride(First).OrderedList);
        Assert.Equal(OrderedListStyle.Plain, first.Load().PreviewStyleFiles.GetOverride(Second).OrderedList);
    }

    [Fact]
    public void 捕获_调用方字典后续变更不污染待保存快照()
    {
        Dictionary<string, PreviewStyleOverrides> mutable = new() { [First] = Style(OrderedListStyle.Circle) };
        ApplicationSettings captured = ApplicationSettingsChanges.Capture(new() { PreviewStyleFiles = new() { FileOverrides = mutable } });
        mutable.Clear();
        Assert.Equal(OrderedListStyle.Circle, captured.PreviewStyleFiles.GetOverride(First).OrderedList);
    }

    [Fact]
    public void 工作区重命名_迁移该目录文件样式但不匹配相似前缀()
    {
        string from = Path.GetFullPath("styles-old"), to = Path.GetFullPath("styles-new");
        string child = Path.Combine(from, "nested", "a.md"), outside = from + "-other" + Path.DirectorySeparatorChar + "b.md";
        ApplicationSettings settings = new()
        {
            PreviewStyleFiles = new PreviewStyleFileSettings()
            .WithOverride(child, Style(OrderedListStyle.Circle)).WithOverride(outside, Style(OrderedListStyle.Plain))
        };
        ApplicationSettings moved = settings.RelocateRecentFiles(from, to);
        Assert.Null(moved.PreviewStyleFiles.GetOverride(child).OrderedList);
        Assert.Equal(OrderedListStyle.Circle, moved.PreviewStyleFiles.GetOverride(Path.Combine(to, "nested", "a.md")).OrderedList);
        Assert.Equal(OrderedListStyle.Plain, moved.PreviewStyleFiles.GetOverride(outside).OrderedList);
        Assert.Equal(OrderedListStyle.Circle, settings.PreviewStyleFiles.GetOverride(child).OrderedList);
    }
}
