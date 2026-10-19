using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhoIsMarkdown.App.Services;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

public sealed class BackgroundImageStoreTests
{
    [Fact]
    public async Task Import_SourceMoved_ManagedCopyStillLoads()
    {
        using BackgroundTestDirectory directory = new();
        string source = directory.CreatePicture("原图.png");
        string managed = Path.Combine(directory.Path, "WIMD", "Backgrounds");
        StoredBackgroundImage stored = await new BackgroundImageStore(managed).ImportAsync(source, 100, 100, TestContext.Current.CancellationToken);
        File.Move(source, System.IO.Path.Combine(directory.Path, "已移动.png"));

        Assert.Equal(managed, System.IO.Path.GetDirectoryName(stored.Path));
        Assert.True(File.Exists(stored.Path));
        Assert.True(stored.Image.IsFrozen);
        Assert.Equal(2, BackgroundImageLoader.Load(stored.Path, 100, 100).PixelWidth);
    }

    [Fact]
    public async Task Import_SameSourceTwice_CopiesDoNotOverwriteEachOther()
    {
        using BackgroundTestDirectory directory = new();
        string source = directory.CreatePicture("背景.png");
        byte[] original = await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken);
        BackgroundImageStore store = new(System.IO.Path.Combine(directory.Path, "Backgrounds"));
        StoredBackgroundImage first = await store.ImportAsync(source, 100, 100, TestContext.Current.CancellationToken);
        StoredBackgroundImage second = await store.ImportAsync(source, 100, 100, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(original, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(first.Path, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(second.Path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(first.Path)!, "*.tmp"));
    }

    [Fact]
    public async Task Import_CorruptPicture_DoesNotPublishFileOrLeaveTemporaryCopy()
    {
        using BackgroundTestDirectory directory = new();
        string source = System.IO.Path.Combine(directory.Path, "invalid.png");
        await File.WriteAllTextAsync(source, "not a picture", TestContext.Current.CancellationToken);
        string managed = System.IO.Path.Combine(directory.Path, "Backgrounds");

        await Assert.ThrowsAnyAsync<Exception>(() => new BackgroundImageStore(managed).ImportAsync(source, 100, 100, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(managed));
        Assert.Equal("not a picture", await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Import_Canceled_DoesNotTouchSourceOrCreateCopy()
    {
        using BackgroundTestDirectory directory = new();
        string source = directory.CreatePicture("cancel.png");
        string managed = System.IO.Path.Combine(directory.Path, "Backgrounds");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackgroundImageStore(managed).ImportAsync(source, 100, 100, new CancellationToken(true)));
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(managed));
    }

    [Fact]
    public async Task Import_UnsupportedExtension_DoesNotCreateDirectory()
    {
        using BackgroundTestDirectory directory = new();
        string managed = System.IO.Path.Combine(directory.Path, "Backgrounds");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new BackgroundImageStore(managed).ImportAsync(System.IO.Path.Combine(directory.Path, "unsafe.svg"), 100, 100, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(managed));
    }

    [Fact]
    public async Task Import_TooLarge_RejectsBeforeCopyAndPreservesSource()
    {
        using BackgroundTestDirectory directory = new();
        string source = System.IO.Path.Combine(directory.Path, "large.png");
        using (FileStream file = File.Create(source)) file.SetLength(BackgroundImageStore.MaximumImageBytes + 1);
        string managed = System.IO.Path.Combine(directory.Path, "Backgrounds");
        await Assert.ThrowsAsync<IOException>(() => new BackgroundImageStore(managed).ImportAsync(source, 100, 100, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(managed));
        Assert.Equal(BackgroundImageStore.MaximumImageBytes + 1, new FileInfo(source).Length);
    }

    [Fact]
    public async Task Import_TargetIsFile_ReportsFailureWithoutChangingSource()
    {
        using BackgroundTestDirectory directory = new();
        string source = directory.CreatePicture("source.png");
        string managed = System.IO.Path.Combine(directory.Path, "blocked");
        await File.WriteAllTextAsync(managed, "keep", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => new BackgroundImageStore(managed).ImportAsync(source, 100, 100, TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(managed, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(source));
    }

    private sealed class BackgroundTestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("WIMD-BackgroundTests-").FullName;

        public string CreatePicture(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            BitmapSource source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 10, 60, 100, 255, 10, 60, 100, 255, 10, 60, 100, 255, 10, 60, 100, 255 }, 8);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
