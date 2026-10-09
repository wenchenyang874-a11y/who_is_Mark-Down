using System.IO;
using System.Windows.Media.Imaging;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Bug fix: full-resolution wallpaper decoding used to block the dispatcher and
/// repeat on every theme change. Decode a display-sized, frozen bitmap on a worker;
/// OnLoad detaches it from the file before publication. The 4096-pixel edge bound
/// caps the retained 32-bit pixel buffer at 64 MiB, even on very large desktops.
/// </summary>
internal static class BackgroundImageLoader
{
    public static BitmapImage Load(string path, int viewportWidth, int viewportHeight)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        double scale = Math.Min(1, Math.Max(
            (double)viewportWidth / frame.PixelWidth,
            (double)viewportHeight / frame.PixelHeight));
        scale = Math.Min(scale, 4096d / Math.Max(frame.PixelWidth, frame.PixelHeight));
        stream.Position = 0;
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (frame.PixelWidth >= frame.PixelHeight)
        {
            image.DecodePixelWidth = Math.Max(1, (int)Math.Ceiling(frame.PixelWidth * scale));
        }
        else
        {
            image.DecodePixelHeight = Math.Max(1, (int)Math.Ceiling(frame.PixelHeight * scale));
        }

        image.EndInit();
        image.Freeze();
        return image;
    }
}
