namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Repairs the invalid all-zero alpha channel produced by some Windows
/// screenshot clipboard providers while preserving every color byte.
/// </summary>
public static class Bgra32AlphaNormalizer
{
    private const int BytesPerPixel = 4;
    private const int AlphaOffset = 3;

    /// <summary>
    /// Makes a BGRA32 buffer opaque only when every pixel has a zero alpha
    /// value. A buffer containing any non-zero alpha is left unchanged so
    /// legitimate transparent and semi-transparent images keep their intent.
    /// </summary>
    /// <param name="pixels">A complete, tightly packed BGRA32 pixel buffer.</param>
    /// <returns><see langword="true"/> when the alpha channel was repaired.</returns>
    public static bool RestoreOpaqueAlphaWhenMissing(Span<byte> pixels)
    {
        if (pixels.Length % BytesPerPixel != 0)
        {
            throw new ArgumentException(
                "BGRA32 pixel data must contain exactly four bytes per pixel.",
                nameof(pixels));
        }

        if (pixels.IsEmpty)
        {
            return false;
        }

        for (int index = AlphaOffset; index < pixels.Length; index += BytesPerPixel)
        {
            if (pixels[index] != 0)
            {
                return false;
            }
        }

        for (int index = AlphaOffset; index < pixels.Length; index += BytesPerPixel)
        {
            pixels[index] = byte.MaxValue;
        }

        return true;
    }
}
