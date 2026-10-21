using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates the independent preview image window. Sources are validated and
/// materialized in the background after the modeless window opens, so the editor stays usable while
/// the viewer supports native window movement, minimizing and maximizing.
/// </summary>
public partial class MainWindow
{
    private const string OpenPreviewImageErrorTitle = "无法打开预览图片";

    private const string ExportPreviewImageErrorTitle = "无法导出预览图片";

    private readonly PreviewImageSaveService previewImageSaveService = new();
    private PreviewImageExportService? previewImageExportService;
    private IPreviewImageRasterizer? previewImageRasterizer;
    private CancellationTokenSource? previewImageOpenCancellation;
    private CancellationTokenSource? previewImageSaveCancellation;
    private PreviewImageWindow? previewImageWindow;
    private PreparedPreviewImage? previewImageBeingSaved;
    private long previewImageOpenVersion;
    private bool previewImageExportRunning;

    /// <summary>
    /// Export rules layered on the image save service, so a prepared picture is
    /// revalidated by the same code that guards an ordinary save. Created on first use
    /// because an instance field initializer cannot reference another instance field.
    /// </summary>
    private PreviewImageExportService PreviewImageExport =>
        previewImageExportService ??= new PreviewImageExportService(previewImageSaveService);

    /// <summary>
    /// Save-as entries for the viewer: the picture's own encoding, a PNG copy and a
    /// self-contained HTML page. Built from the core export rules so the toolbar
    /// button and the viewer's right-click menu cannot drift apart again.
    /// </summary>
    private static IReadOnlyList<PreviewImageExportOption> CreateExportOptions(
        PreparedPreviewImage image) =>
        PreviewImageExportService.GetAvailableFormats(image)
            .Select(format => new PreviewImageExportOption(
                format,
                PreviewImageExportService.GetDisplayName(image, format)))
            .ToArray();

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
        using DiagnosticOperation? viewing = DiagnosticLog.Current?.Begin(DiagnosticStage.ImageViewer, diagnosticWindowId);
        DiagnosticLog.Current?.Mark(DiagnosticStage.ImageRequestReceived, diagnosticWindowId, requestVersion);
        DiagnosticLog.Current?.Mark(DiagnosticStage.ImageRequestOrigin, diagnosticWindowId,
            previewImageRefreshRequested ? 2 : (long)eventArgs.Origin);
        PreviewImageWindow? requestedViewer = null;

        try
        {
            requestedViewer = BeginPreviewImageLoading(eventArgs.AlternativeText, viewing?.Id ?? 0);
            // Let the loading window paint before starting I/O. A visible shell is
            // not image readiness; the request below remains open through decode/fit.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
            string? documentPath = document.FilePath;
            RemoteImagePolicy policy = CreateRemoteImagePolicy();
            PreviewImageSaveService images = previewImageSaveService;
            PreviewImageSaveSource source = await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageSourceResolve,
                () => ResolveViewerSourceAsync(images, eventArgs.Source, documentPath, eventArgs.AlternativeText,
                    eventArgs.IsGeneratedDiagram, policy, cancellationToken), diagnosticWindowId);
            DiagnosticLog.Current?.Mark(DiagnosticStage.ImageSourceKind, diagnosticWindowId,
                source.RequiresNetwork ? 2 : eventArgs.IsGeneratedDiagram ? 4 : eventArgs.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? 3 : 1);
            cacheDirectory = Path.Combine(GetPreviewImageCacheRoot(), Guid.NewGuid().ToString("N"));
            CachedPreviewImage? cached = source.RequiresNetwork && previewService is { } preview
                ? await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageCacheLookup,
                    () => preview.TryReadCachedImageAsync(eventArgs.Source, cancellationToken), diagnosticWindowId)
                : null;
            if (source.RequiresNetwork) DiagnosticLog.Current?.Mark(cached is null ? DiagnosticStage.ImageCacheMiss : DiagnosticStage.ImageCacheHit,
                diagnosticWindowId, cached?.EntryId ?? 0);
            UpdateStatus(source.RequiresNetwork && cached is null ? "正在后台下载图片…" : "正在载入图片…");
            string targetDirectory = cacheDirectory;
            PreparedPreviewImage preparedImage = await DiagnosticLog.MeasureAsync(DiagnosticStage.ImagePrepare,
                () => PrepareViewerImageAsync(images, source, cached, targetDirectory, cancellationToken), diagnosticWindowId);

            if (windowClosed
                || cancellationToken.IsCancellationRequested
                || requestVersion != Volatile.Read(ref previewImageOpenVersion)
                || !ReferenceEquals(previewImageWindow, requestedViewer) || !requestedViewer.CanLoadAnotherImage)
            {
                DiagnosticLog.Current?.Mark(DiagnosticStage.ImageViewerSuperseded, diagnosticWindowId, requestVersion);
                viewing?.Cancel();
                TryDeletePreviewImageCache(cacheDirectory);
                return;
            }

            if (cached is null && source.RequiresNetwork && previewService is { } activePreview)
                _ = activePreview.RememberPreparedImageAsync(eventArgs.Source, preparedImage);
            DiagnosticLog.Measure(DiagnosticStage.ImageViewerApply,
                () => ObtainPreviewImageWindow(preparedImage, eventArgs.AlternativeText), diagnosticWindowId);
            cacheDirectory = null;
            await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageViewerReady,
                () => requestedViewer.WaitForImageReadyAsync(cancellationToken), diagnosticWindowId);
            if (windowClosed || cancellationToken.IsCancellationRequested || requestVersion != Volatile.Read(ref previewImageOpenVersion))
            {
                DiagnosticLog.Current?.Mark(DiagnosticStage.ImageViewerSuperseded, diagnosticWindowId, requestVersion);
                viewing?.Cancel();
                return;
            }
            DiagnosticLog.Current?.Record(DiagnosticStage.ImageViewerReadyDispatch, DiagnosticOutcome.Point, diagnosticWindowId,
                duration: requestedViewer.ReadyDispatchMilliseconds);
            // A reload of the open viewer arrives through this same path, so the
            // status has to describe what the user actually asked for.
            bool refreshed = previewImageRefreshRequested;
            previewImageRefreshRequested = false;
            UpdateStatus(refreshed ? "已刷新图片查看窗口" : "已在独立窗口中打开图片");
            viewing?.Succeed();
        }
        catch (OperationCanceledException) when (windowClosed || cancellationToken.IsCancellationRequested)
        {
            viewing?.Cancel();
            // A newer image request or application shutdown superseded this load.
        }
        catch (PreviewImageSaveException exception)
        {
            if (windowClosed || cancellationToken.IsCancellationRequested || requestVersion != Volatile.Read(ref previewImageOpenVersion)) { viewing?.Cancel(); return; }
            viewing?.Fail(exception);
            requestedViewer?.ReportLoadFailure(exception);
            ShowPreviewImageError(exception.Message, this, OpenPreviewImageErrorTitle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            if (windowClosed || cancellationToken.IsCancellationRequested || requestVersion != Volatile.Read(ref previewImageOpenVersion)) { viewing?.Cancel(); return; }
            viewing?.Fail(exception);
            if (exception is TimeoutException) previewImageOpenCancellation?.Cancel();
            requestedViewer?.ReportLoadFailure(exception);
            ShowPreviewImageError($"无法打开预览图片：{exception.Message}", this, OpenPreviewImageErrorTitle);
        }
        finally
        {
            if (cacheDirectory is not null)
            {
                TryDeletePreviewImageCache(cacheDirectory);
            }
        }
    }

    private PreviewImageWindow BeginPreviewImageLoading(string? alternativeText, long parent)
    {
        if (previewImageWindow is not { CanLoadAnotherImage: true })
        {
            ClosePreviewImageWindow();
            previewImageWindow = DiagnosticLog.Measure(DiagnosticStage.ImageViewerAllocation,
                () => new PreviewImageWindow(alternativeText), diagnosticWindowId);
            previewImageWindow.ExportRequested += PreviewImageWindow_ExportRequested;
            previewImageWindow.Closed += PreviewImageWindow_Closed;
        }
        PreviewImageWindow viewer = previewImageWindow;
        viewer.BeginLoading(alternativeText, diagnosticWindowId, parent);
        DiagnosticLog.Measure(DiagnosticStage.ImageViewerShow, () =>
        {
            if (!viewer.IsVisible) viewer.Show();
            RestoreAndActivate(viewer);
        }, diagnosticWindowId);
        return viewer;
    }

    private static async Task<PreparedPreviewImage> PrepareViewerImageAsync(PreviewImageSaveService images,
        PreviewImageSaveSource source, CachedPreviewImage? cached, string directory, CancellationToken cancellationToken)
    {
        // Only pure data/service objects cross the worker boundary. Cancellation
        // stops the UI wait, not a filesystem driver; late work cleans its own copy.
        Task<PreparedPreviewImage> work = Task.Run(async () =>
        {
            try
            {
                return cached is null
                    ? await images.PrepareAsync(source, directory, cancellationToken).ConfigureAwait(false)
                    : await images.PrepareCachedAsync(source, cached.Bytes, cached.ContentType, directory, cancellationToken).ConfigureAwait(false);
            }
            finally { if (cancellationToken.IsCancellationRequested) TryDeletePreviewImageCache(directory); }
        }, CancellationToken.None);
        _ = work.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await work.WaitAsync(TimeSpan.FromSeconds(65), cancellationToken).ConfigureAwait(false);
    }

    // A separate static scope prevents the compiler from merging this worker's
    // closure with the UI handler's lambdas (which also capture the Window/viewer).
    private static Task<PreviewImageSaveSource> ResolveViewerSourceAsync(PreviewImageSaveService images, string source,
        string? documentPath, string? alternativeText, bool generated, RemoteImagePolicy policy, CancellationToken cancellationToken) =>
        Task.Run(() => generated ? images.ResolveGeneratedSvgDataUri(source, alternativeText)
            : images.Resolve(source, documentPath, alternativeText, policy), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>
    /// Bug fix: opening another image used to close the current viewer and create a
    /// new window, so the user lost the window placement, size and maximized state
    /// and saw a close/open flash. The open viewer now takes the new image in place.
    /// Only a viewer that cannot be reused — none yet, still starting up, failed to
    /// initialize, or already closing — is replaced by a fresh window.
    /// </summary>
    private void ObtainPreviewImageWindow(
        PreparedPreviewImage preparedImage,
        string? alternativeText)
    {
        if (previewImageWindow is { } existing && existing.CanLoadAnotherImage)
        {
            try
            {
                PreparedPreviewImage? replaced = existing.LoadImage(
                    preparedImage,
                    alternativeText);
                existing.SetExportOptions(CreateExportOptions(preparedImage));
                if (replaced is not null)
                {
                    // The available formats follow the picture: a bitmap source cannot
                    // be saved as SVG, a diagram cannot be saved as JPEG.
                    ReleaseReplacedPreviewImageCache(replaced);
                }
                // Activation belongs to the user's request, not its asynchronous
                // completion. Do not undo a minimize or steal focus during download.
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or System.Runtime.InteropServices.COMException)
            {
                // The in-place swap failed before it could navigate, so the existing
                // window still shows the previous image. Replace it with a live one
                // instead of leaving the user with a viewer that cannot switch.
                UpdateStatus($"无法在现有查看窗口中切换图片，已改为新开窗口：{exception.Message}");
                ClosePreviewImageWindow();
            }
        }

        ClosePreviewImageWindow();
        PreviewImageWindow viewer = new(preparedImage, alternativeText);
        viewer.ExportRequested += PreviewImageWindow_ExportRequested;
        viewer.Closed += PreviewImageWindow_Closed;
        viewer.SetExportOptions(CreateExportOptions(preparedImage));
        previewImageWindow = viewer;

        // Deliberately do not assign Owner or call ShowDialog. The viewer must
        // remain a genuine modeless top-level window so both windows stay usable.
        viewer.Show();
        viewer.Activate();
    }

    private static void RestoreAndActivate(PreviewImageWindow viewer)
    {
        if (viewer.WindowState == WindowState.Minimized)
        {
            // A minimized viewer must come back before Activate, otherwise the
            // refreshed image stays invisible behind the taskbar button.
            viewer.WindowState = WindowState.Normal;
        }

        viewer.Activate();
    }

    /// <summary>
    /// Releases the cache directory of an image the viewer no longer displays. An
    /// image that is still being saved keeps its bytes until the copy completes.
    /// </summary>
    private void ReleaseReplacedPreviewImageCache(PreparedPreviewImage replaced)
    {
        if (!ReferenceEquals(previewImageBeingSaved, replaced))
        {
            TryDeletePreviewImageCache(Path.GetDirectoryName(replaced.FilePath));
        }
    }

    private async void PreviewImageWindow_ExportRequested(
        object? sender,
        PreviewImageExportRequestedEventArgs eventArgs)
    {
        if (sender is not PreviewImageWindow viewer)
        {
            return;
        }

        await RunExportGuardedAsync(() => ExportPreviewImageAsync(
            eventArgs.Image,
            eventArgs.Format,
            viewer.IsVisible ? viewer : this,
            viewer.IsVisible ? viewer : null));
    }

    /// <summary>
    /// Runs one export interaction so that nothing can escape an async void handler.
    ///
    /// Bug fix (crash on export): an exception leaving an async void handler terminates
    /// the process, and WIMD hosts several windows in one process, so a failed export
    /// closed every open window and discarded the unsaved documents in all of them. The
    /// failure therefore has to stay a reported error.
    /// </summary>
    private async Task RunExportGuardedAsync(Func<Task> export)
    {
        try
        {
            await export();
        }
        catch (Exception exception)
        {
            string message = $"导出预览图片时发生意外错误：{exception.Message}";
            UpdateStatus(message);
            if (!windowClosed)
            {
                MessageBox.Show(
                    this,
                    message,
                    "无法导出预览图片",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// Saves an image picked in the live preview. The preview only knows the picture's
    /// URL, so the image is resolved, validated and materialized again before the
    /// export runs, exactly as opening the viewer would.
    /// </summary>
    private async void PreviewService_PreviewContextImageExportRequested(
        object? sender,
        PreviewContextImageExportRequestedEventArgs eventArgs)
    {
        await RunExportGuardedAsync(() => ExportContextImageAsync(eventArgs));
    }

    private async Task ExportContextImageAsync(
        PreviewContextImageExportRequestedEventArgs eventArgs)
    {
        if (previewImageExportRunning)
        {
            UpdateStatus("正在导出上一张预览图片，请稍候…");
            return;
        }

        string cacheDirectory = Path.Combine(GetPreviewImageCacheRoot(), Guid.NewGuid().ToString("N"));
        PreparedPreviewImage? preparedImage = null;
        try
        {
            UpdateStatus("正在准备预览图片…");
            PreviewImageSaveSource source = eventArgs.IsGeneratedDiagram
                ? previewImageSaveService.ResolveGeneratedSvgDataUri(
                    eventArgs.Source,
                    null)
                : previewImageSaveService.Resolve(
                    eventArgs.Source,
                    document.FilePath,
                    null,
                    CreateRemoteImagePolicy());
            preparedImage = await previewImageSaveService.PrepareAsync(
                source,
                cacheDirectory,
                CancellationToken.None);
        }
        catch (PreviewImageSaveException exception)
        {
            ShowPreviewImageError(exception.Message, this, ExportPreviewImageErrorTitle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowPreviewImageError($"无法读取预览图片：{exception.Message}", this, ExportPreviewImageErrorTitle);
        }

        if (preparedImage is null)
        {
            TryDeletePreviewImageCache(cacheDirectory);
            return;
        }

        try
        {
            await ExportPreviewImageAsync(preparedImage, eventArgs.Format, this, null);
        }
        finally
        {
            // The materialized copy exists only for this export. The viewer keeps its
            // own cache directory, so nothing else references these bytes.
            TryDeletePreviewImageCache(cacheDirectory);
        }
    }

    /// <summary>
    /// Writes the chosen format to the user's destination.
    ///
    /// Bug fix: saving used to always reuse the picture's own encoding, so the
    /// right-click menu produced an HTML page while the toolbar produced an SVG. Both
    /// surfaces now run through this single path and let the user pick the format.
    /// </summary>
    private async Task ExportPreviewImageAsync(
        PreparedPreviewImage preparedImage,
        PreviewImageExportFormat format,
        Window owner,
        PreviewImageWindow? viewer)
    {
        ArgumentNullException.ThrowIfNull(preparedImage);
        if (previewImageExportRunning)
        {
            UpdateStatus("正在导出上一张预览图片，请稍候…");
            return;
        }

        string extension = PreviewImageExportService.GetExtension(preparedImage, format);
        SaveFileDialog dialog = new()
        {
            Title = "预览图片另存为",
            Filter = BuildExportFilter(extension, format),
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            ValidateNames = true,
            FileName = PreviewImageExportService.GetSuggestedFileName(preparedImage, format),
            InitialDirectory = GetPreviewImageInitialDirectory(),
        };
        if (!owner.IsVisible || dialog.ShowDialog(owner) != true)
        {
            return;
        }

        // Copy the dialog value before asynchronous work. WPF dialog state must
        // never be read from a continuation that may outlive the owner window.
        string targetPath = dialog.FileName;
        previewImageExportRunning = true;
        previewImageBeingSaved = preparedImage;
        previewImageSaveCancellation = new CancellationTokenSource();
        try
        {
            UpdateStatus("正在保存预览图片…");
            bool saved = await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageExport, () => PreviewImageExport.ExportAsync(
                preparedImage,
                format,
                targetPath,
                GetPreviewImageRasterizer(),
                previewImageSaveCancellation.Token), diagnosticWindowId);
            UpdateStatus(saved ? $"图片已保存：{targetPath}" : "图片已位于所选位置");
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            // Closing the application cancels an in-flight atomic write.
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("预览图片保存已取消");
        }
        catch (PreviewImageSaveException exception)
        {
            ShowPreviewImageError(
                exception.Message,
                viewer?.IsVisible == true ? viewer : owner,
                ExportPreviewImageErrorTitle);
        }
        finally
        {
            previewImageSaveCancellation?.Dispose();
            previewImageSaveCancellation = null;
            previewImageExportRunning = false;
            previewImageBeingSaved = null;
            if (viewer is { IsVisible: false })
            {
                ReleaseReplacedPreviewImageCache(preparedImage);
            }
        }
    }

    private static string BuildExportFilter(string extension, PreviewImageExportFormat format) =>
        format switch
        {
            PreviewImageExportFormat.Html => "HTML 网页 (*.html)|*.html",
            PreviewImageExportFormat.Png => "PNG 图片 (*.png)|*.png",
            _ => $"图片 (*{extension})|*{extension}",
        };

    /// <summary>
    /// Lazily creates the offscreen WebView2 rasterizer. Building it starts a browser
    /// surface, so it is only created when the user actually exports a raster format.
    /// </summary>
    private IPreviewImageRasterizer GetPreviewImageRasterizer() =>
        previewImageRasterizer ??= new WebView2ImageRasterizer(WebView2UserDataFolder.Resolve());

    private void PreviewImageWindow_Closed(object? sender, EventArgs eventArgs)
    {
        if (sender is not PreviewImageWindow viewer)
        {
            return;
        }

        viewer.ExportRequested -= PreviewImageWindow_ExportRequested;
        viewer.Closed -= PreviewImageWindow_Closed;
        if (ReferenceEquals(previewImageWindow, viewer))
        {
            previewImageWindow = null;
            previewImageOpenCancellation?.Cancel();
        }

        if (viewer.PreparedImageOrNull is { } image) ReleaseReplacedPreviewImageCache(image);
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

    /// <summary>
    /// Reports a preview image failure. The title names the operation that failed, because
    /// an export failure used to be reported as "无法打开预览图片", which sent the user
    /// looking at the wrong thing.
    /// </summary>
    private void ShowPreviewImageError(string message, Window owner, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        UpdateStatus(message);
        MessageBox.Show(
            owner,
            message,
            title,
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
        if (!string.IsNullOrWhiteSpace(cacheDirectory)) _ = Task.Run(() => TryDeletePreviewImageCacheCore(cacheDirectory));
    }

    private static void TryDeletePreviewImageCacheCore(string cacheDirectory)
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
