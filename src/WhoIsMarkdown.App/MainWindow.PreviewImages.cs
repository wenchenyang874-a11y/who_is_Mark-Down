using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates the independent preview image window. Sources are validated and
/// materialized before a modeless window opens, so the editor stays usable while
/// the viewer supports native window movement, minimizing and maximizing.
/// </summary>
public partial class MainWindow
{
    private readonly PreviewImageSaveService previewImageSaveService = new();
    private CancellationTokenSource? previewImageOpenCancellation;
    private CancellationTokenSource? previewImageSaveCancellation;
    private PreviewImageWindow? previewImageWindow;
    private PreparedPreviewImage? previewImageBeingSaved;
    private long previewImageOpenVersion;
    private bool previewImageSaveRunning;

    private async void PreviewService_PreviewImageOpenRequested(
        object? sender,
        PreviewImageOpenRequestedEventArgs eventArgs)
    {
        long requestVersion = Interlocked.Increment(ref previewImageOpenVersion);
        previewImageOpenCancellation?.Cancel();
        previewImageOpenCancellation?.Dispose();
        previewImageOpenCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = previewImageOpenCancellation.Token;
        string? cacheDirectory = null;

        try
        {
            PreviewImageSaveSource source = previewImageSaveService.Resolve(
                eventArgs.Source,
                document.FilePath,
                eventArgs.AlternativeText,
                CreateRemoteImagePolicy());
            cacheDirectory = Path.Combine(GetPreviewImageCacheRoot(), Guid.NewGuid().ToString("N"));
            UpdateStatus(source.RequiresNetwork ? "正在下载图片并打开查看器…" : "正在打开图片查看器…");
            PreparedPreviewImage preparedImage = await previewImageSaveService.PrepareAsync(
                source,
                cacheDirectory,
                cancellationToken);

            if (windowClosed
                || cancellationToken.IsCancellationRequested
                || requestVersion != Volatile.Read(ref previewImageOpenVersion))
            {
                TryDeletePreviewImageCache(cacheDirectory);
                return;
            }

            ClosePreviewImageWindow();
            PreviewImageWindow viewer = new(preparedImage, eventArgs.AlternativeText);
            viewer.SaveRequested += PreviewImageWindow_SaveRequested;
            viewer.Closed += PreviewImageWindow_Closed;
            previewImageWindow = viewer;

            // Deliberately do not assign Owner or call ShowDialog. The viewer must
            // remain a genuine modeless top-level window so both windows stay usable.
            viewer.Show();
            viewer.Activate();
            UpdateStatus("已在独立窗口中打开图片");
            cacheDirectory = null;
        }
        catch (OperationCanceledException) when (windowClosed || cancellationToken.IsCancellationRequested)
        {
            // A newer image request or application shutdown superseded this load.
        }
        catch (PreviewImageSaveException exception)
        {
            ShowPreviewImageError(exception.Message, this);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowPreviewImageError($"无法打开预览图片：{exception.Message}", this);
        }
        finally
        {
            if (cacheDirectory is not null)
            {
                TryDeletePreviewImageCache(cacheDirectory);
            }
        }
    }

    private async void PreviewImageWindow_SaveRequested(object? sender, EventArgs eventArgs)
    {
        if (sender is not PreviewImageWindow viewer)
        {
            return;
        }

        if (previewImageSaveRunning)
        {
            UpdateStatus("正在保存上一张预览图片，请稍候…");
            return;
        }

        PreparedPreviewImage preparedImage = viewer.PreparedImage;
        SaveFileDialog dialog = new()
        {
            Title = "预览图片另存为",
            Filter = $"图片 (*{preparedImage.Extension})|*{preparedImage.Extension}",
            DefaultExt = preparedImage.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            ValidateNames = true,
            FileName = preparedImage.SuggestedFileName,
            InitialDirectory = GetPreviewImageInitialDirectory(),
        };
        if (dialog.ShowDialog(viewer) != true)
        {
            return;
        }

        // Copy the dialog value before asynchronous work. WPF dialog state must
        // never be read from a continuation that may outlive the viewer window.
        string targetPath = dialog.FileName;
        previewImageSaveRunning = true;
        previewImageBeingSaved = preparedImage;
        previewImageSaveCancellation = new CancellationTokenSource();
        try
        {
            UpdateStatus("正在保存预览图片…");
            bool saved = await previewImageSaveService.SavePreparedAsync(
                preparedImage,
                targetPath,
                previewImageSaveCancellation.Token);
            UpdateStatus(saved ? $"图片已保存：{targetPath}" : "图片已位于所选位置");
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            // Closing the application cancels an in-flight atomic copy.
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("预览图片保存已取消");
        }
        catch (PreviewImageSaveException exception)
        {
            ShowPreviewImageError(exception.Message, viewer.IsVisible ? viewer : this);
        }
        finally
        {
            previewImageSaveCancellation?.Dispose();
            previewImageSaveCancellation = null;
            previewImageSaveRunning = false;
            previewImageBeingSaved = null;
            if (!viewer.IsVisible)
            {
                TryDeletePreviewImageCache(Path.GetDirectoryName(preparedImage.FilePath));
            }
        }
    }

    private void PreviewImageWindow_Closed(object? sender, EventArgs eventArgs)
    {
        if (sender is not PreviewImageWindow viewer)
        {
            return;
        }

        viewer.SaveRequested -= PreviewImageWindow_SaveRequested;
        viewer.Closed -= PreviewImageWindow_Closed;
        if (ReferenceEquals(previewImageWindow, viewer))
        {
            previewImageWindow = null;
        }

        if (!ReferenceEquals(previewImageBeingSaved, viewer.PreparedImage))
        {
            TryDeletePreviewImageCache(Path.GetDirectoryName(viewer.PreparedImage.FilePath));
        }
    }

    private string GetPreviewImageInitialDirectory()
    {
        if (document.FilePath is not null)
        {
            string? documentDirectory = Path.GetDirectoryName(document.FilePath);
            if (documentDirectory is not null && Directory.Exists(documentDirectory))
            {
                return documentDirectory;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private static string GetPreviewImageCacheRoot()
    {
        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WIMD",
            "ImageViewerCache"));
    }

    private void ShowPreviewImageError(string message, Window owner)
    {
        UpdateStatus(message);
        MessageBox.Show(
            owner,
            message,
            "无法打开预览图片",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ClosePreviewImageWindow()
    {
        PreviewImageWindow? viewer = previewImageWindow;
        if (viewer is null)
        {
            return;
        }

        previewImageWindow = null;
        viewer.Close();
    }

    private static void TryDeletePreviewImageCache(string? cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
        {
            return;
        }

        try
        {
            string root = GetPreviewImageCacheRoot();
            string target = Path.GetFullPath(cacheDirectory);
            string relative = Path.GetRelativePath(root, target);
            bool isDirectChild = !Path.IsPathFullyQualified(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.Contains(Path.DirectorySeparatorChar)
                && !relative.Contains(Path.AltDirectorySeparatorChar);
            if (isDirectChild)
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A WebView or atomic save may still have the cache open briefly. The
            // directory contains only validated temporary image bytes and is retried
            // after save completion or removed by a later WIMD session.
        }
    }

    private void CancelPreviewImageWork()
    {
        previewImageOpenCancellation?.Cancel();
        previewImageOpenCancellation?.Dispose();
        previewImageOpenCancellation = null;
        previewImageSaveCancellation?.Cancel();
        ClosePreviewImageWindow();
        previewImageSaveService.Dispose();
    }
}
