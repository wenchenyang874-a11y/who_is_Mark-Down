using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Reads both WPF bitmap data and the custom PNG clipboard format used by common
/// screenshot tools such as WeChat. Clipboard access is retried because another
/// process can briefly retain the Windows clipboard immediately after capture.
/// </summary>
public static class ClipboardImageReader
{
    private const string PngClipboardFormat = "PNG";

    public static bool ContainsImage()
    {
        try
        {
            IDataObject? dataObject = Clipboard.GetDataObject();
            return dataObject?.GetDataPresent(PngClipboardFormat) == true
                || dataObject?.GetDataPresent(DataFormats.Bitmap) == true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static async Task<BitmapSource?> ReadAsync(CancellationToken cancellationToken = default)
    {
        const int attempts = 4;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadOnce();
            }
            catch (ExternalException) when (attempt < attempts)
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(true);
            }
            catch (ExternalException)
            {
                // The clipboard may remain locked by the screenshot tool. Treat
                // that race as "no readable image" instead of crashing Ctrl+V.
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Encodes clipboard pixels as PNG and repairs the specific invalid alpha
    /// channel emitted by some screenshot tools. Those providers can expose
    /// valid RGB bytes with alpha zero for every pixel; preserving that channel
    /// produces a fully transparent upload even though the screenshot is intact.
    /// </summary>
    public static byte[] EncodePng(BitmapSource bitmapSource)
    {
        ArgumentNullException.ThrowIfNull(bitmapSource);

        BitmapSource bgraSource = ConvertToBgra32(bitmapSource);
        int stride = checked(bgraSource.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgraSource.PixelHeight)];
        bgraSource.CopyPixels(pixels, stride, 0);
        Bgra32AlphaNormalizer.RestoreOpaqueAlphaWhenMissing(pixels);

        double dpiX = bgraSource.DpiX > 0 ? bgraSource.DpiX : 96;
        double dpiY = bgraSource.DpiY > 0 ? bgraSource.DpiY : 96;
        BitmapSource normalizedBitmap = BitmapSource.Create(
            bgraSource.PixelWidth,
            bgraSource.PixelHeight,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(normalizedBitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource ConvertToBgra32(BitmapSource bitmapSource)
    {
        if (bitmapSource.Format == PixelFormats.Bgra32)
        {
            return bitmapSource;
        }

        FormatConvertedBitmap converted = new(
            bitmapSource,
            PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        if (converted.CanFreeze)
        {
            converted.Freeze();
        }

        return converted;
    }

    private static BitmapSource? ReadOnce()
    {
        IDataObject? dataObject = Clipboard.GetDataObject();
        if (dataObject is null)
        {
            return null;
        }

        if (dataObject.GetDataPresent(PngClipboardFormat)
            && dataObject.GetData(PngClipboardFormat) is Stream pngStream)
        {
            using MemoryStream copy = new();
            pngStream.CopyTo(copy);
            copy.Position = 0;
            PngBitmapDecoder decoder = new(
                copy,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            return frame;
        }

        if (dataObject.GetDataPresent(DataFormats.Bitmap)
            && dataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmapSource)
        {
            if (bitmapSource.CanFreeze)
            {
                bitmapSource.Freeze();
            }

            return bitmapSource;
        }

        return Clipboard.GetImage();
    }
}
