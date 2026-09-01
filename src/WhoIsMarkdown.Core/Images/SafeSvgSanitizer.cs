using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Converts user-supplied SVG into a bounded, non-interactive static profile.
/// Scripts, animation, external resources, event handlers and unknown markup are
/// removed before bytes are exposed to either WebView2 or the document directory.
/// </summary>
public static class SafeSvgSanitizer
{
    public const int MaximumSvgBytes = 8 * 1024 * 1024;

    private const int MaximumAttributeValueLength = 1024 * 1024;
    private const int MaximumAttributes = 200_000;
    private const int MaximumDepth = 128;
    private const int MaximumElements = 50_000;
    private const string SvgNamespaceName = "http://www.w3.org/2000/svg";
    private const string XLinkNamespaceName = "http://www.w3.org/1999/xlink";
    private const string XmlNamespaceName = "http://www.w3.org/XML/1998/namespace";

    private static readonly XNamespace SvgNamespace = SvgNamespaceName;

    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "style", "symbol", "use", "switch",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop", "pattern", "clipPath", "mask",
        "marker",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "id", "class", "role", "aria-label", "aria-labelledby", "aria-describedby",
        "version", "baseProfile", "viewBox", "preserveAspectRatio", "width", "height",
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "points", "pathLength", "transform", "gradientTransform", "patternTransform",
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-linecap",
        "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset",
        "stroke-opacity", "opacity", "color", "color-interpolation", "color-rendering",
        "shape-rendering", "text-rendering", "image-rendering", "vector-effect", "paint-order",
        "clip-path", "clip-rule", "mask", "marker-start", "marker-mid", "marker-end",
        "display", "visibility", "overflow", "font-family", "font-size", "font-style",
        "font-weight", "font-variant", "font-stretch", "text-anchor", "dominant-baseline",
        "alignment-baseline", "baseline-shift", "letter-spacing", "word-spacing",
        "writing-mode", "direction", "unicode-bidi", "text-decoration", "dx", "dy",
        "rotate", "textLength", "lengthAdjust", "startOffset", "method", "spacing",
        "offset", "stop-color", "stop-opacity", "gradientUnits", "spreadMethod", "fx",
        "fy", "fr", "patternUnits", "patternContentUnits", "markerHeight", "markerWidth",
        "refX", "refY", "markerUnits", "orient", "clipPathUnits", "maskUnits",
        "maskContentUnits", "href", "style",
    };

    public static async Task<SafeSvgSanitizationResult> SanitizeFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string path = Path.GetFullPath(filePath);
        FileInfo file = new(path);
        if (!file.Exists)
        {
            throw new SafeSvgException("SVG 文件不存在。");
        }

        if (file.Length is <= 0 or > MaximumSvgBytes)
        {
            throw new SafeSvgException("SVG 文件为空或超过 8 MB。");
        }

        try
        {
            await using FileStream input = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using MemoryStream buffer = new((int)Math.Min(file.Length, MaximumSvgBytes));
            byte[] chunk = new byte[81920];
            int total = 0;
            while (true)
            {
                int read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumSvgBytes)
                {
                    throw new SafeSvgException("SVG 文件超过 8 MB。");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return Sanitize(buffer.ToArray());
        }
        catch (SafeSvgException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new SafeSvgException($"无法读取 SVG：{exception.Message}", exception);
        }
    }

    public static SafeSvgSanitizationResult Sanitize(byte[] sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(sourceBytes);
        if (sourceBytes.Length is 0 or > MaximumSvgBytes)
        {
            throw new SafeSvgException("SVG 文件为空或超过 8 MB。");
        }

        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaximumSvgBytes,
            };
            using MemoryStream input = new(sourceBytes, writable: false);
            using XmlReader reader = XmlReader.Create(input, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement root = document.Root
                ?? throw new SafeSvgException("SVG 缺少根元素。");
            if (root.Name != SvgNamespace + "svg")
            {
                throw new SafeSvgException("文件不是有效的 SVG 2 文档。");
            }

            List<XElement> elements = root.DescendantsAndSelf().ToList();
            if (elements.Count > MaximumElements)
            {
                throw new SafeSvgException($"SVG 元素数量超过 {MaximumElements:N0} 个安全上限。");
            }

            foreach (XElement element in elements)
            {
                if (element.Ancestors().Take(MaximumDepth + 1).Count() > MaximumDepth)
                {
                    throw new SafeSvgException($"SVG 嵌套深度超过 {MaximumDepth} 层安全上限。");
                }
            }

            int removedElements = RemoveUnsafeElements(root);
            SvgAttributeRemovalCounts attributeRemoval = RemoveUnsafeAttributes(root);
            removedElements += attributeRemoval.RemovedElements;
            RemoveNonVisualXmlNodes(document);
            byte[] output = Serialize(document);
            if (output.Length > MaximumSvgBytes)
            {
                throw new SafeSvgException("安全过滤后的 SVG 超过 8 MB。");
            }

            return new SafeSvgSanitizationResult(
                output,
                removedElements,
                attributeRemoval.RemovedAttributes);
        }
        catch (SafeSvgException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException
            or InvalidOperationException
            or ArgumentException
            or RegexMatchTimeoutException)
        {
            throw new SafeSvgException("SVG 无法通过安全静态解析。", exception);
        }
    }

    private static int RemoveUnsafeElements(XElement root)
    {
        int removed = 0;
        foreach (XElement element in root.Descendants().Reverse().ToList())
        {
            if (element.Name.NamespaceName != SvgNamespaceName
                || !AllowedElements.Contains(element.Name.LocalName))
            {
                element.Remove();
                removed++;
            }
        }

        return removed;
    }

    private static SvgAttributeRemovalCounts RemoveUnsafeAttributes(XElement root)
    {
        int removedAttributes = 0;
        int removedElements = 0;
        int totalAttributes = 0;
        foreach (XElement element in root.DescendantsAndSelf().ToList())
        {
            foreach (XAttribute attribute in element.Attributes().ToList())
            {
                totalAttributes++;
                if (totalAttributes > MaximumAttributes)
                {
                    throw new SafeSvgException($"SVG 属性数量超过 {MaximumAttributes:N0} 个安全上限。");
                }

                if (!IsSafeAttribute(attribute))
                {
                    attribute.Remove();
                    removedAttributes++;
                }
            }

            if (element.Name.LocalName == "style" && HasUnsafeCss(element.Value))
            {
                element.Remove();
                removedElements++;
            }
        }

        return new SvgAttributeRemovalCounts(removedAttributes, removedElements);
    }

    private static bool IsSafeAttribute(XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
        {
            return attribute.Value == SvgNamespaceName
                || (attribute.Name.LocalName == "xlink" && attribute.Value == XLinkNamespaceName);
        }

        string localName = attribute.Name.LocalName;
        string namespaceName = attribute.Name.NamespaceName;
        string value = attribute.Value.Trim();
        if (value.Length > MaximumAttributeValueLength
            || localName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (namespaceName == XmlNamespaceName)
        {
            return localName is "lang" or "space";
        }

        if (namespaceName.Length > 0 && namespaceName != XLinkNamespaceName)
        {
            return false;
        }

        if (!AllowedAttributes.Contains(localName))
        {
            return false;
        }

        if (localName == "href")
        {
            return IsFragmentReference(value);
        }

        return (localName != "style"
                && !value.Contains("url", StringComparison.OrdinalIgnoreCase))
            || !HasUnsafeCss(value);
    }

    private static bool IsFragmentReference(string value)
    {
        return Regex.IsMatch(
            value,
            "^#[A-Za-z0-9_.:-]+$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
    }

    private static bool HasUnsafeCss(string value)
    {
        if (value.Contains('\\')
            || value.Contains("/*", StringComparison.Ordinal)
            || Regex.IsMatch(
                value,
                "@|expression\\s*\\(|javascript\\s*:|vbscript\\s*:|data\\s*:|https?\\s*:|file\\s*:|behavior\\s*:|-moz-binding",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)))
        {
            return true;
        }

        foreach (Match match in Regex.Matches(
                     value,
                     "url\\s*\\(([^)]*)\\)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                     TimeSpan.FromMilliseconds(50)))
        {
            string target = match.Groups[1].Value.Trim().Trim('\'', '"');
            if (!IsFragmentReference(target))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveNonVisualXmlNodes(XDocument document)
    {
        document.Declaration = null;
        document.Nodes().OfType<XProcessingInstruction>().Remove();
        document.DescendantNodes().OfType<XComment>().Remove();
        document.DescendantNodes().OfType<XProcessingInstruction>().Remove();
    }

    private static byte[] Serialize(XDocument document)
    {
        using MemoryStream output = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
        };
        using (XmlWriter writer = XmlWriter.Create(output, settings))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }

    private readonly record struct SvgAttributeRemovalCounts(
        int RemovedAttributes,
        int RemovedElements);
}
