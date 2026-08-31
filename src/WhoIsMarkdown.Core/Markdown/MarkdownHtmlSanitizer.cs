using System.Globalization;
using AngleSharp.Dom;
using Ganss.Xss;

namespace WhoIsMarkdown.Core.Markdown;

/// <summary>
/// Applies WIMD's explicit raw-HTML allowlist after Markdig rendering. The library
/// parser handles malformed markup safely; this policy removes executable content,
/// inline CSS, event handlers, unsafe URL schemes, and unsupported attributes.
/// </summary>
public sealed class MarkdownHtmlSanitizer
{
    private static readonly HashSet<string> SafeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "blockquote", "br", "code", "del", "details", "div", "dl", "dt", "dd",
        "em", "figcaption", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "img",
        "input", "ins", "kbd", "li", "mark", "ol", "p", "pre", "q", "s", "samp", "small",
        "span", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "tr", "u", "ul", "var", "wbr",
    };

    private static readonly HashSet<string> SafeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "align", "alt", "checked", "class", "colspan", "disabled", "headers", "height", "href",
        "id", "open", "rowspan", "scope", "src", "start", "title", "type", "width",
    };

    private static readonly HashSet<string> KnownClasses = new(StringComparer.Ordinal)
    {
        "contains-task-list", "footnote-backref", "footnote-ref", "footnotes", "math", "mermaid",
        "task-list-item",
    };

    private readonly HtmlSanitizer sanitizer;
    private readonly object syncRoot = new();

    public MarkdownHtmlSanitizer()
    {
        sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(SafeTags);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(SafeAttributes);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.UnionWith(["http", "https", "mailto", "data"]);
        sanitizer.UriAttributes.Clear();
        sanitizer.UriAttributes.UnionWith(["href", "src"]);
        sanitizer.PostProcessNode += OnPostProcessNode;
    }

    public string Sanitize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        // Debounced renders may briefly overlap when an obsolete Task.Run cannot
        // be canceled mid-parse. HtmlSanitizer owns a DOM parser, so serialize use.
        lock (syncRoot)
        {
            return sanitizer.Sanitize(html);
        }
    }

    private static void OnPostProcessNode(object? sender, PostProcessNodeEventArgs eventArgs)
    {
        if (eventArgs.Node is not IElement element)
        {
            return;
        }

        string tag = element.LocalName;
        foreach (IAttr attribute in element.Attributes.ToArray())
        {
            if (!IsAttributeAllowedForTag(tag, attribute.LocalName))
            {
                element.RemoveAttribute(attribute.LocalName);
            }
        }

        NormalizeClassAttribute(element);
        NormalizeAlignmentAttribute(element);
        NormalizeDimensionAttribute(element, "width", allowPercentage: true);
        NormalizeDimensionAttribute(element, "height", allowPercentage: false);
        NormalizePositiveIntegerAttribute(element, "colspan", maximum: 100);
        NormalizePositiveIntegerAttribute(element, "rowspan", maximum: 100);
        NormalizePositiveIntegerAttribute(element, "start", maximum: int.MaxValue);
        NormalizeLink(element);
        NormalizeImageSource(element);
        NormalizeCheckbox(element);
    }

    private static bool IsAttributeAllowedForTag(string tag, string attribute) => attribute switch
    {
        "id" or "class" or "title" => true,
        "align" => tag is "div" or "p" or "table" or "td" or "th",
        "href" => tag is "a",
        "src" or "alt" => tag is "img",
        "width" => tag is "img" or "table" or "td" or "th",
        "height" => tag is "img" or "td" or "th",
        "checked" or "disabled" or "type" => tag is "input",
        "open" => tag is "details",
        "colspan" or "rowspan" or "headers" => tag is "td" or "th",
        "scope" => tag is "th",
        "start" => tag is "ol",
        _ => false,
    };

    private static void NormalizeClassAttribute(IElement element)
    {
        string? value = element.GetAttribute("class");
        if (value is null)
        {
            return;
        }

        string[] safeClasses = value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(candidate => IsSafeClass(element.LocalName, candidate))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (safeClasses.Length == 0)
        {
            element.RemoveAttribute("class");
            return;
        }

        element.SetAttribute("class", string.Join(' ', safeClasses));
    }

    private static bool IsSafeClass(string tag, string value)
    {
        if (string.Equals(value, "mermaid", StringComparison.Ordinal))
        {
            return string.Equals(tag, "pre", StringComparison.OrdinalIgnoreCase);
        }

        if (KnownClasses.Contains(value))
        {
            return true;
        }

        const string languagePrefix = "language-";
        return value.StartsWith(languagePrefix, StringComparison.Ordinal)
            && value.Length <= 80
            && value.AsSpan(languagePrefix.Length).IndexOfAnyExcept(
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_+-#".AsSpan()) < 0;
    }

    private static void NormalizeAlignmentAttribute(IElement element)
    {
        string? value = element.GetAttribute("align")?.Trim().ToLowerInvariant();
        if (value is null)
        {
            return;
        }

        if (value is not ("left" or "center" or "right" or "justify"))
        {
            element.RemoveAttribute("align");
            return;
        }

        element.SetAttribute("align", value);
    }

    private static void NormalizeDimensionAttribute(
        IElement element,
        string attribute,
        bool allowPercentage)
    {
        string? value = element.GetAttribute(attribute)?.Trim();
        if (value is null)
        {
            return;
        }

        bool isPercentage = value.EndsWith('%');
        string numericPart = isPercentage ? value[..^1] : value;
        int maximum = isPercentage ? 100 : 4096;
        if ((isPercentage && !allowPercentage)
            || !int.TryParse(numericPart, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 1
            || parsed > maximum)
        {
            element.RemoveAttribute(attribute);
            return;
        }

        element.SetAttribute(attribute, isPercentage ? $"{parsed}%" : parsed.ToString(CultureInfo.InvariantCulture));
    }

    private static void NormalizePositiveIntegerAttribute(IElement element, string attribute, int maximum)
    {
        string? value = element.GetAttribute(attribute)?.Trim();
        if (value is null)
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 1
            || parsed > maximum)
        {
            element.RemoveAttribute(attribute);
            return;
        }

        element.SetAttribute(attribute, parsed.ToString(CultureInfo.InvariantCulture));
    }

    private static void NormalizeLink(IElement element)
    {
        if (element.LocalName != "a")
        {
            return;
        }

        string? href = element.GetAttribute("href");
        if (href?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            element.RemoveAttribute("href");
        }
    }

    private static void NormalizeImageSource(IElement element)
    {
        if (element.LocalName != "img")
        {
            return;
        }

        string? source = element.GetAttribute("src");
        if (source?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true)
        {
            element.RemoveAttribute("src");
        }
    }

    private static void NormalizeCheckbox(IElement element)
    {
        if (element.LocalName != "input")
        {
            return;
        }

        element.SetAttribute("type", "checkbox");
        element.SetAttribute("disabled", string.Empty);
    }
}
