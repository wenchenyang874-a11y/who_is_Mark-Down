using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App;

/// <summary>
/// Coordinates the complete image insertion pipeline. Clipboard WPF objects are
/// converted to immutable PNG bytes on the UI thread before any asynchronous file
/// or network work, preventing the cross-thread DispatcherObject bug class.
/// </summary>
public partial class MainWindow
{
    private readonly LocalImageStorageService localImageStorageService = new();
    private readonly IImageHostClient imageHostClient = new ImgBbImageHostClient();
    private readonly ISecretProtector secretProtector = new WindowsDpapiSecretProtector();
    private CancellationTokenSource? imageOperationCancellation;
    private bool imageOperationRunning;

    private async void InsertImage_Click(object sender, RoutedEventArgs eventArgs)
    {
        OpenFileDialog dialog = new()
        {
            Title = "选择要插入的图片",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string selectedPath = dialog.FileName;
        string altText = Path.GetFileNameWithoutExtension(selectedPath);
        await RunImageOperationAsync(async cancellationToken =>
        {
            if (applicationSettings.ImageInsertion.StorageMode == ImageStorageMode.ImgBb)
            {
                await UploadFileAndInsertAsync(selectedPath, altText, cancellationToken);
            }
            else
            {
                await StoreFileAndInsertAsync(selectedPath, altText, cancellationToken);
            }
        });
    }

    private void ImageSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        ShowImageSettingsDialog();
    }

    private bool ShowImageSettingsDialog()
    {
        ImageSettingsWindow dialog = new(applicationSettings.ImageInsertion) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        ImageInsertionSettings settings = dialog.ResultSettings;
        if (dialog.NewApiKey is not null)
        {
            try
            {
                settings = settings with
                {
                    ProtectedImgBbApiKey = secretProtector.Protect(dialog.NewApiKey),
                };
            }
            catch (SecretProtectionException exception)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "无法保存 ImgBB API Key",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        applicationSettings = applicationSettings with { ImageInsertion = settings.Normalize() };
        if (TrySaveApplicationSettings())
        {
            UpdateStatus("图片设置已保存");
        }

        // A trust-policy change also changes the preview page CSP. Scheduling one
        // refresh lets PreviewWebViewService perform the required one-time reload.
        SchedulePreview();
        return true;
    }

    private async Task PasteClipboardImageAsync()
    {
        await RunImageOperationAsync(async cancellationToken =>
        {
            System.Windows.Media.Imaging.BitmapSource? bitmap =
                await ClipboardImageReader.ReadAsync(cancellationToken);
            if (bitmap is null)
            {
                UpdateStatus("剪贴板中没有可读取的图片");
                return;
            }

            byte[] pngBytes = ClipboardImageReader.EncodePng(bitmap);
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string altText = $"截图-{timestamp}";
            if (applicationSettings.ImageInsertion.StorageMode == ImageStorageMode.ImgBb)
            {
                await UploadBytesAndInsertAsync(
                    pngBytes,
                    $"wimd-{timestamp}.png",
                    altText,
                    cancellationToken);
            }
            else
            {
                await StoreBytesAndInsertAsync(
                    pngBytes,
                    $"image-{timestamp}",
                    altText,
                    cancellationToken);
            }
        });
    }

    private async Task StoreFileAndInsertAsync(
        string sourcePath,
        string altText,
        CancellationToken cancellationToken)
    {
        if (!await EnsureDocumentHasImageDirectoryAsync())
        {
            return;
        }

        UpdateStatus("正在保存图片…");
        StoredLocalImage image = await LocalImageStorageService.StoreFileAsync(
            document.FilePath!,
            applicationSettings.ImageInsertion.LocalDirectory,
            sourcePath,
            cancellationToken);
        InsertMarkdownImage(MarkdownImageFormatter.CreateLocal(altText, image.MarkdownPath));
        UpdateStatus($"图片已保存到 {image.MarkdownPath}");
    }

    private async Task StoreBytesAndInsertAsync(
        byte[] pngBytes,
        string preferredName,
        string altText,
        CancellationToken cancellationToken)
    {
        if (!await EnsureDocumentHasImageDirectoryAsync())
        {
            return;
        }

        UpdateStatus("正在保存剪贴板图片…");
        StoredLocalImage image = await LocalImageStorageService.StorePngAsync(
            document.FilePath!,
            applicationSettings.ImageInsertion.LocalDirectory,
            preferredName,
            pngBytes,
            cancellationToken);
        InsertMarkdownImage(MarkdownImageFormatter.CreateLocal(altText, image.MarkdownPath));
        UpdateStatus($"剪贴板图片已保存到 {image.MarkdownPath}");
    }

    private async Task UploadFileAndInsertAsync(
        string sourcePath,
        string altText,
        CancellationToken cancellationToken)
    {
        string? apiKey = GetImgBbApiKey();
        if (apiKey is null)
        {
            return;
        }

        UpdateStatus("正在上传图片到 ImgBB…");
        await using FileStream stream = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        HostedImage image = await imageHostClient.UploadAsync(
            stream,
            Path.GetFileName(sourcePath),
            apiKey,
            cancellationToken);
        CompleteHostedImageInsertion(altText, image);
    }

    private async Task UploadBytesAndInsertAsync(
        byte[] imageBytes,
        string fileName,
        string altText,
        CancellationToken cancellationToken)
    {
        string? apiKey = GetImgBbApiKey();
        if (apiKey is null)
        {
            return;
        }

        UpdateStatus("正在上传剪贴板图片到 ImgBB…");
        using MemoryStream stream = new(imageBytes, writable: false);
        HostedImage image = await imageHostClient.UploadAsync(
            stream,
            fileName,
            apiKey,
            cancellationToken);
        CompleteHostedImageInsertion(altText, image);
    }

    private void CompleteHostedImageInsertion(string altText, HostedImage image)
    {
        InsertMarkdownImage(MarkdownImageFormatter.CreateRemote(altText, image.Url));
        bool visible = CreateRemoteImagePolicy().Allows(image.Url);
        UpdateStatus(visible
            ? "图片已上传到 ImgBB 并插入文档"
            : "图片已上传并插入；当前远程图片策略会在预览中阻止该地址");
    }

    private async Task<bool> EnsureDocumentHasImageDirectoryAsync()
    {
        if (document.FilePath is not null)
        {
            return true;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "本地图片目录以当前 Markdown 文件的位置为基准。请先保存文档，再插入图片。是否现在保存？",
            "请先保存 Markdown 文档",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        return result == MessageBoxResult.Yes
            && await SaveCurrentDocumentAsync(forceSaveAs: false);
    }

    private string? GetImgBbApiKey()
    {
        string? protectedApiKey = applicationSettings.ImageInsertion.ProtectedImgBbApiKey;
        if (string.IsNullOrWhiteSpace(protectedApiKey))
        {
            MessageBox.Show(
                this,
                "请先在“图片设置”中填写自己的 ImgBB API Key。",
                "尚未配置 ImgBB",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ShowImageSettingsDialog();
            protectedApiKey = applicationSettings.ImageInsertion.ProtectedImgBbApiKey;
            if (string.IsNullOrWhiteSpace(protectedApiKey))
            {
                return null;
            }
        }

        try
        {
            return secretProtector.Unprotect(protectedApiKey);
        }
        catch (SecretProtectionException exception)
        {
            UpdateStatus(exception.Message);
            MessageBox.Show(
                this,
                exception.Message,
                "无法读取 ImgBB API Key",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
    }

    private RemoteImagePolicy CreateRemoteImagePolicy()
    {
        ImageInsertionSettings settings = applicationSettings.ImageInsertion.Normalize();
        return new RemoteImagePolicy(settings.TrustMode, settings.RemoteImageRules);
    }

    private void InsertMarkdownImage(string markdown)
    {
        int insertionStart = Editor.SelectionStart;
        Editor.Document.BeginUpdate();
        try
        {
            Editor.Document.Replace(insertionStart, Editor.SelectionLength, markdown);
            Editor.Select(insertionStart + markdown.Length, 0);
            Editor.CaretOffset = insertionStart + markdown.Length;
        }
        finally
        {
            Editor.Document.EndUpdate();
        }

        Editor.Focus();
    }

    private async Task RunImageOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (imageOperationRunning)
        {
            UpdateStatus("正在处理上一张图片，请稍候…");
            return;
        }

        imageOperationRunning = true;
        SetImageActionsEnabled(enabled: false);
        imageOperationCancellation = new CancellationTokenSource();
        try
        {
            await operation(imageOperationCancellation.Token);
        }
        catch (OperationCanceledException) when (windowClosed)
        {
            // Closing the window cancels an in-flight file copy or HTTP upload.
        }
        catch (OperationCanceledException)
        {
            UpdateStatus("图片处理已取消或上传超时");
        }
        catch (Exception exception) when (exception is LocalImageStorageException
            or ImageHostUploadException
            or IOException
            or UnauthorizedAccessException)
        {
            UpdateStatus(exception.Message);
            MessageBox.Show(
                this,
                exception.Message,
                "无法插入图片",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            imageOperationCancellation?.Dispose();
            imageOperationCancellation = null;
            imageOperationRunning = false;
            if (!windowClosed)
            {
                SetImageActionsEnabled(enabled: true);
            }
        }
    }

    private void SetImageActionsEnabled(bool enabled)
    {
        InsertImageMenuItem.IsEnabled = enabled;
        InsertImageToolbarButton.IsEnabled = enabled;
    }

    private void CancelImageWork()
    {
        imageOperationCancellation?.Cancel();
        imageHostClient.Dispose();
    }
}
