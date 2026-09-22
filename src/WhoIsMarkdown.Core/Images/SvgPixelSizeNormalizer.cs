using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Resolves an SVG's own pixel size and writes it back as explicit width and height
/// attributes.
///
/// Mermaid emits width="100%" together with a viewBox, and WIMD's sanitizer removes
/// the height attribute, so the file has no usable intrinsic size. Chromium then falls
/// back to its 300x150 default object size: the image element reported 300x40 for a
/// 1200-wide diagram. The independent viewer trusted that number and produced a 345%
/// "fit" pinned to the left edge, and a standalone HTML export rendered the same
/// diagram only a few hundred pixels wide.
///
/// Only the rendered image can be measured, and an image element cannot read the
/// vector's viewBox back, so the size is resolved once here while the data is still
/// XML. Every consumer — viewer display, HTML export and PNG rasterization — then sees
/// a file whose intrinsic dimensions are real.
/// </summary>
internal static class SvgPixelSizeNormalizer
{
    /// <summary>
    /// Used only when an SVG declares neither an absolute size nor a viewBox. Both
    /// values are required by the SVG root element, so real content always supplies one.
    /// </summary>
    private const int FallbackWidth = 1024;
    private const int FallbackHeight = 768;

    public static (byte[] Bytes, int Width, int Height) Apply(byte[] svgBytes)
    {
        ArgumentNullException.ThrowIfNull(svgBytes);
        XDocument document = Load(svgBytes);
        XElement root = document.Root
            ?? throw new PreviewImageSaveException("矢量图缺少根元素，无法确定显示尺寸。");
        (double width, double height) = ResolveSize(root);

        int pixelWidth = ToPixelCount(width);
        int pixelHeight = ToPixelCount(height);
        root.SetAttributeValue("width", pixelWidth.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", pixelHeight.ToString(CultureInfo.InvariantCulture));

        using MemoryStream stream = new();
        // No XML declaration and no reformatting: the only intended difference from the
        // sanitized input is the two size attributes, which keeps the materialized copy
        // and any later "save as SVG" close to what the preview already rendered.
        XmlWriterSettings writerSettings = new()
        {
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
        };
        using (XmlWriter writer = XmlWriter.Create(stream, writerSettings))
        {
            document.Save(writer);
        }

        return (stream.ToArray(), pixelWidth, pixelHeight);
    }

    private static XDocument Load(byte[] svgBytes)
    {
        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = PreviewImageSaveService.MaximumImageBytes,
            };
            using MemoryStream stream = new(svgBytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new PreviewImageSaveException("矢量图内容无法解析。", exception);
        }
    }

    /// <summary>
    /// Prefers an absolute width and height, because that is the size the vector
    /// declares. A percentage or unitless value has no pixel meaning, so the viewBox
    /// supplies the extent in its own coordinate system, which is what Mermaid relies on.
    /// </summary>
    private static (double Width, double Height) ResolveSize(XElement root)
    {
        double width = ParseAbsoluteLength(root.Attribute("width")?.Value);
        double height = ParseAbsoluteLength(root.Attribute("height")?.Value);
        if (width > 0 && height > 0)
        {
            return (width, height);
        }

        string[] viewBox = (root.Attribute("viewBox")?.Value ?? string.Empty)
            .Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (viewBox.Length == 4
            && double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double viewBoxWidth)
            && double.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double viewBoxHeight)
            && viewBoxWidth > 0
            && viewBoxHeight > 0)
        {
            return (viewBoxWidth, viewBoxHeight);
        }

        return (FallbackWidth, FallbackHeight);
    }

    private static double ParseAbsoluteLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        string candidate = value.Trim();
        if (candidate.EndsWith('%'))
        {
            return 0;
        }

        if (candidate.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^2].Trim();
        }

        return double.TryParse(
                candidate,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double length)
            && length > 0
                ? length
                : 0;
    }

    private static int ToPixelCount(double length) =>
        Math.Max(1, (int)Math.Round(length));
}
