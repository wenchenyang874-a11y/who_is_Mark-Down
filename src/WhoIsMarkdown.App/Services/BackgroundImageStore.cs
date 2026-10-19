using System.IO;
using System.Windows.Media.Imaging;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Bug fix: persisting the selected source path made a wallpaper disappear after
/// its owner moved it. Import an independent, validated local copy before exposing
/// its path. Never overwrite/delete the source or remove other windows' copies.
/// The caller runs this complete operation off the dispatcher, including opening
/// files and decoding. Cancellation does not forcibly interrupt native disk I/O.
/// </summary>
internal sealed class BackgroundImageStore(string directory)
{
    internal const long MaximumImageBytes = 32L * 1024 * 1024;

    private readonly string managedDirectory = Path.GetFullPath(directory);

    public async Task<StoredBackgroundImage> ImportAsync(
        string sourcePath, int viewportWidth, int viewportHeight, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            throw new NotSupportedException("背景仅支持 PNG、JPEG 和 BMP 图片。");

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(managedDirectory);
        // A filename generated here, not a user-supplied relative path, owns both
        // temporary and final targets. Atomic publication cannot clobber a source.
        string identity = Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(managedDirectory, $".{identity}.tmp");
        string originalName = Path.GetFileNameWithoutExtension(source);
        if (originalName.Length > 80) originalName = originalName[..80];
        string destination = Path.Combine(managedDirectory, $"{originalName}-{identity}{extension}");
        try
        {
            await CopyBoundedAsync(source, temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Decode our exact copy, not the mutable original. A failed decode
            // leaves the previous setting and bitmap untouched in the caller.
            BitmapImage image = BackgroundImageLoader.Load(temporary, viewportWidth, viewportHeight);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return new StoredBackgroundImage(destination, image);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup must not hide an import failure.
            }
        }
    }

    private static async Task CopyBoundedAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length is <= 0 or > MaximumImageBytes)
            throw new IOException("背景图片不能为空，且不能超过 32 MB。");
        await using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous);
        byte[] buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            copied += count;
            if (copied > MaximumImageBytes) throw new IOException("背景图片超过 32 MB，已取消导入。");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record StoredBackgroundImage(string Path, BitmapImage Image);
