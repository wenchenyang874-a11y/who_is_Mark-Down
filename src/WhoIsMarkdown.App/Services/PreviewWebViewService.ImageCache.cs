using System.Globalization;
using System.IO;
using Microsoft.Web.WebView2.Core;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.App.Services;

public sealed partial class PreviewWebViewService
{
    private readonly PreviewImageResponseCache imageResponseCache = new();
    private string? imageCacheDocumentPath;

    internal Task<CachedPreviewImage?> TryReadCachedImageAsync(string source, CancellationToken cancellationToken) =>
        imageResponseCache.TryReadAsync(source, cancellationToken);

    internal Task RememberPreparedImageAsync(string source, PreparedPreviewImage image)
    {
        string path = image.FilePath;
        string contentType = image.Extension switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
        // A fallback download also seeds the bounded session cache. Open its file
        // off-thread; closing the viewer may delete the path, but a shared-delete
        // stream that is already open remains readable until capture completes.
        return imageResponseCache.CaptureAsync(source, contentType, null,
            () => Task.Run<Stream>(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)), DiagnosticWindowId);
    }

    private void PrepareImageCacheForDocument(string? documentPath, bool force)
    {
        if (!force && string.Equals(imageCacheDocumentPath, documentPath, StringComparison.OrdinalIgnoreCase)) return;
        imageResponseCache.Clear();
        imageCacheDocumentPath = documentPath;
    }

    private void OnImageResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (disposed) return;
        try
        {
            // Observe only remote image responses; no request filters, injected
            // fetch, second download, author scripts or browsing changes.
            string source = args.Request.Uri;
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https") || uri.IdnHost == LocalImageUrlResolver.VirtualHostName) return;
            CoreWebView2WebResourceResponseView response = args.Response;
            if (response.StatusCode is >= 300 and < 400 && response.Headers.Contains("Location")
                && Uri.TryCreate(uri, response.Headers.GetHeader("Location"), out Uri? destination))
            {
                imageResponseCache.RememberRedirect(source, destination.AbsoluteUri);
                return;
            }
            if (response.StatusCode is < 200 or >= 300 || !response.Headers.Contains("Content-Type")) return;
            string contentType = response.Headers.GetHeader("Content-Type");
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;
            long? length = response.Headers.Contains("Content-Length")
                && long.TryParse(response.Headers.GetHeader("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                ? size : null;
            _ = imageResponseCache.CaptureAsync(source, contentType, length, response.GetContentAsync, DiagnosticWindowId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DiagnosticLog.Current?.Error(DiagnosticStage.ImageCacheCapture, exception, DiagnosticWindowId);
        }
    }
}
