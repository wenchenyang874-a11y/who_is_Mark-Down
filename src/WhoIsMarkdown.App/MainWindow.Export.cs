using System.IO;
using System.Windows;
using Microsoft.Win32;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.App;

/// <summary>
/// Exports the current safe preview through WebView2's PDF engine. The PDF is
/// written to a same-directory temporary file first, so a failed render never
/// truncates an existing user-selected destination.
/// </summary>
public partial class MainWindow
{
    private async void ExportPdf_Click(object sender, RoutedEventArgs eventArgs)
    {
        SaveFileDialog dialog = new()
        {
            Title = "导出为 PDF",
            Filter = "PDF 文档 (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(document.DisplayName),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string targetPath = Path.GetFullPath(dialog.FileName);
        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("PDF 目标路径缺少父目录。");
        string temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.tmp.pdf");

        ExportPdfMenuItem.IsEnabled = false;
        using DiagnosticOperation? export = DiagnosticLog.Current?.Begin(DiagnosticStage.PdfExport, diagnosticWindowId);
        UpdateStatus("正在准备最新预览并导出 PDF…");
        try
        {
            await EnsurePreviewInitializedAsync();
            if (windowClosed || previewService is not { } service)
            {
                return;
            }

            CancellationToken cancellationToken = await PrepareLatestPreviewForExportAsync(service);
            await service.ExportPdfAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: true);
            UpdateStatus($"PDF 已导出：{targetPath}");
            export?.Succeed();
        }
        catch (OperationCanceledException)
        {
            export?.Cancel();
            UpdateStatus("PDF 导出已取消；文档在导出期间发生了新的修改");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException
            or System.Runtime.InteropServices.COMException)
        {
            export?.Fail(exception);
            if (windowClosed)
            {
                return;
            }

            UpdateStatus($"PDF 导出失败：{exception.Message}");
            MessageBox.Show(
                this,
                $"无法导出 PDF：\n{targetPath}\n\n{exception.Message}",
                "PDF 导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            TryDeleteTemporaryPdf(temporaryPath);
            if (!windowClosed)
            {
                ExportPdfMenuItem.IsEnabled = true;
            }
        }
    }

    private async Task<CancellationToken> PrepareLatestPreviewForExportAsync(
        PreviewWebViewService service,
        bool forceBodyReplacement = false)
    {
        CancelPreviewWork();
        previewCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = previewCancellation.Token;
        long version = ++previewVersion;
        EditorTextSnapshot snapshot = document.CaptureSnapshot();
        string? documentPath = document.FilePath;
        RemoteImagePolicy remoteImagePolicy = CreateRemoteImagePolicy();

        MarkdownRenderResult rendered = await markdownRenderer.RenderSnapshotAsync(snapshot.ReadText, documentPath, remoteImagePolicy, cancellationToken)
            .WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version != previewVersion)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        SuppressPreviewScrollEcho();
        await service.ShowAsync(
            rendered,
            previewStyleSheet,
            previewAppearanceStyleSheet,
            documentPath,
            remoteImagePolicy,
            synchronizeToCaretWhenReady: false,
            forceBodyReplacement: forceBodyReplacement,
            cancellationToken: cancellationToken);
        await service.WaitUntilReadyAsync(cancellationToken);
        return cancellationToken;
    }

    private static void TryDeleteTemporaryPdf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must not replace the original export result.
        }
    }
}
