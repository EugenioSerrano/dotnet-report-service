namespace ReportService.TemplateChecks.Scanning;

/// <summary>Where in an HTML document a byte of text sits.</summary>
public enum HtmlContextKind
{
    /// <summary>Element text.</summary>
    Text,

    /// <summary>Inside a tag, where an attribute name is expected.</summary>
    Tag,

    /// <summary>Inside a quoted attribute value.</summary>
    AttributeValueQuoted,

    /// <summary>Inside an unquoted attribute value.</summary>
    AttributeValueUnquoted,

    /// <summary>Inside a raw-text script element.</summary>
    Script,

    /// <summary>Inside a raw-text style element.</summary>
    Style,
}

/// <summary>The context one output sits in, and the attribute it belongs to.</summary>
/// <param name="Kind">The context.</param>
/// <param name="AttributeName">The attribute whose value the output sits in, lower-cased; otherwise null.</param>
public sealed record HtmlContextAt(HtmlContextKind Kind, string? AttributeName);

/// <summary>One reference an HTML document or a stylesheet makes the render fetch.</summary>
/// <param name="Url">The reference, as written after trimming and unquoting.</param>
/// <param name="Offset">The character offset it starts at.</param>
/// <param name="Kind">What carries it: an attribute name, or <c>url</c> / <c>import</c> in CSS.</param>
/// <param name="InCss">Whether it comes from CSS rather than an HTML attribute.</param>
/// <param name="Image">Whether the render's content policy admits a <c>data:</c> URL there.</param>
public sealed record AssetReference(string Url, int Offset, string Kind, bool InCss, bool Image);

/// <summary>
/// Reads an HTML document's literal text: the context each position sits in, and
/// the references that make the render fetch something. Template spans are opaque:
/// they are skipped, so an expression is never mistaken for a URL.
/// </summary>
public static class HtmlScanner
{
    private static readonly string[] FetchAttributes =
    [
        "href", "src", "srcset", "action", "formaction", "poster", "data", "xlink:href", "style",
    ];

    private static readonly Dictionary<string, string[]> FetchByTag = new(StringComparer.OrdinalIgnoreCase)
    {
        ["link"] = ["href"],
        ["img"] = ["src", "srcset"],
        ["source"] = ["src", "srcset"],
        ["script"] = ["src"],
        ["iframe"] = ["src"],
        ["embed"] = ["src"],
        ["audio"] = ["src"],
        ["video"] = ["src", "poster"],
        ["track"] = ["src"],
        ["input"] = ["src"],
        ["object"] = ["data"],
        ["image"] = ["href", "xlink:href"],
        ["use"] = ["href", "xlink:href"],
    };

    /// <summary>The context <paramref name="offset"/> sits in.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="offset">The character offset.</param>
    /// <param name="regions">The spans that are template syntax.</param>
    public static HtmlContextAt ContextAt(string text, int offset, IReadOnlyList<TemplateRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regions);

        var inTag = false;
        var inComment = false;
        var rawText = string.Empty;
        var tagName = string.Empty;
        var attrName = string.Empty;
        var readingTagName = false;
        var readingAttrName = false;
        var inValue = false;
        var quote = '\0';
        var regionIndex = 0;

        for (var index = 0; index < offset && index < text.Length;)
        {
            if (regionIndex < regions.Count && regions[regionIndex].Start == index)
            {
                index = regions[regionIndex].Start + regions[regionIndex].Length;
                regionIndex++;
                continue;
            }

            if (inComment)
            {
                if (StartsWith(text, index, "-->"))
                {
                    inComment = false;
                    index += 3;
                    continue;
                }

                index++;
                continue;
            }

            var character = text[index];
            if (rawText.Length > 0)
            {
                if (character == '<' && StartsWith(text, index, "</" + rawText, ignoreCase: true))
                {
                    rawText = string.Empty;
                }

                index++;
                continue;
            }

            if (inValue)
            {
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        inValue = false;
                        quote = '\0';
                    }
                }
                else if (character is ' ' or '>' or '\t' or '\r' or '\n')
                {
                    inValue = false;
                }

                index++;
                continue;
            }

            if (!inTag)
            {
                if (StartsWith(text, index, "<!--"))
                {
                    inComment = true;
                    index += 4;
                    continue;
                }

                if (character == '<' && index + 1 < text.Length && (char.IsLetter(text[index + 1]) || text[index + 1] == '/'))
                {
                    inTag = true;
                    tagName = string.Empty;
                    attrName = string.Empty;
                    readingTagName = true;
                    index++;
                    continue;
                }

                index++;
                continue;
            }

            if (character == '>')
            {
                inTag = false;
                readingTagName = false;
                readingAttrName = false;
                if (tagName.Equals("script", StringComparison.OrdinalIgnoreCase))
                {
                    rawText = "script";
                }
                else if (tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    rawText = "style";
                }

                index++;
                continue;
            }

            if (readingTagName)
            {
                if (char.IsWhiteSpace(character) || character == '/')
                {
                    readingTagName = false;
                }
                else if (char.IsLetterOrDigit(character))
                {
                    tagName += character;
                }

                index++;
                continue;
            }

            if (character == '=')
            {
                var next = index + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                if (next < text.Length && (text[next] == '"' || text[next] == '\''))
                {
                    quote = text[next];
                    inValue = true;
                    index = next + 1;
                    continue;
                }

                inValue = true;
                quote = '\0';
                index = next;
                continue;
            }

            if (character is '"' or '\'')
            {
                index++;
                continue;
            }

            if (char.IsWhiteSpace(character) || character == '/')
            {
                readingAttrName = false;
                index++;
                continue;
            }

            if (!readingAttrName)
            {
                readingAttrName = true;
                attrName = string.Empty;
            }

            if (char.IsLetterOrDigit(character) || character is '-' or ':' or '_')
            {
                attrName += character;
            }

            index++;
        }

        var lowerAttribute = attrName.Length > 0 ? attrName.ToLowerInvariant() : null;
        if (rawText.Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            return new HtmlContextAt(HtmlContextKind.Script, null);
        }

        if (rawText.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            return new HtmlContextAt(HtmlContextKind.Style, null);
        }

        if (inValue)
        {
            return new HtmlContextAt(
                quote == '\0' ? HtmlContextKind.AttributeValueUnquoted : HtmlContextKind.AttributeValueQuoted,
                lowerAttribute);
        }

        return inTag
            ? new HtmlContextAt(HtmlContextKind.Tag, lowerAttribute)
            : new HtmlContextAt(HtmlContextKind.Text, null);
    }

    /// <summary>Whether an attribute must never carry a value derived from the records.</summary>
    /// <param name="attributeName">The attribute's lower-cased name, when known.</param>
    public static bool IsDangerousAttribute(string? attributeName)
    {
        if (string.IsNullOrEmpty(attributeName))
        {
            return false;
        }

        if (attributeName.StartsWith("on", StringComparison.Ordinal))
        {
            return true;
        }

        return attributeName is "href" or "src" or "srcset" or "action" or "formaction" or "poster" or "data"
            or "xlink:href" or "style";
    }

    /// <summary>The references the document makes the render fetch.</summary>
    /// <param name="text">The document's text.</param>
    /// <param name="regions">The spans that are template syntax.</param>
    public static IReadOnlyList<AssetReference> Assets(string text, IReadOnlyList<TemplateRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(regions);

        var references = new List<AssetReference>();
        var index = 0;
        var regionIndex = 0;
        while (index < text.Length)
        {
            if (regionIndex < regions.Count && regions[regionIndex].Start == index)
            {
                index = regions[regionIndex].Start + regions[regionIndex].Length;
                regionIndex++;
                continue;
            }

            if (StartsWith(text, index, "<!--"))
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 3;
                continue;
            }

            if (text[index] == '<' && index + 1 < text.Length && char.IsLetter(text[index + 1]))
            {
                var end = FindTagEnd(text, index, regions);
                if (end < 0)
                {
                    break;
                }

                var tag = text[index..(end + 1)];
                ReadTag(tag, index, text, regions, references);
                index = end + 1;
                continue;
            }

            index++;
        }

        return references;
    }

    private static void ReadTag(
        string tag,
        int offset,
        string text,
        IReadOnlyList<TemplateRegion> regions,
        List<AssetReference> references)
    {
        var name = string.Empty;
        var index = 1;
        while (index < tag.Length && (char.IsLetterOrDigit(tag[index]) || tag[index] == '-'))
        {
            name += tag[index];
            index++;
        }

        if (name.Length == 0)
        {
            return;
        }

        var attributes = ReadAttributes(tag, index, offset, text, regions);
        foreach (var (attribute, value, at) in attributes)
        {
            if (attribute.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var reference in CssScanner.Urls(value))
                {
                    references.Add(new AssetReference(reference, at, "style", InCss: true, Image: true));
                }

                continue;
            }

            var fetch = FetchByTag.TryGetValue(name, out var wanted)
                && Array.Exists(wanted, candidate => candidate.Equals(attribute, StringComparison.OrdinalIgnoreCase));
            if (!fetch)
            {
                continue;
            }

            if (attribute.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var candidate in value.Split(','))
                {
                    var url = candidate.Trim().Split(' ')[0];
                    if (url.Length > 0)
                    {
                        references.Add(new AssetReference(url, at, "srcset", InCss: false, Image: true));
                    }
                }

                continue;
            }

            if (value.Length > 0)
            {
                var image = name.Equals("img", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("source", StringComparison.OrdinalIgnoreCase);
                references.Add(new AssetReference(value, at, attribute, InCss: false, Image: image));
            }
        }
    }

    private static List<(string Name, string Value, int Offset)> ReadAttributes(
        string tag,
        int index,
        int offset,
        string text,
        IReadOnlyList<TemplateRegion> regions)
    {
        var attributes = new List<(string, string, int)>();
        while (index < tag.Length)
        {
            while (index < tag.Length && (char.IsWhiteSpace(tag[index]) || tag[index] == '/'))
            {
                index++;
            }

            var name = string.Empty;
            while (index < tag.Length && (char.IsLetterOrDigit(tag[index]) || tag[index] is '-' or ':' or '_'))
            {
                name += tag[index];
                index++;
            }

            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            var value = string.Empty;
            var valueOffset = offset + index;
            if (index < tag.Length && tag[index] == '=')
            {
                index++;
                while (index < tag.Length && char.IsWhiteSpace(tag[index]))
                {
                    index++;
                }

                var quote = index < tag.Length && (tag[index] == '"' || tag[index] == '\'') ? tag[index] : '\0';
                if (quote != '\0')
                {
                    index++;
                    valueOffset = offset + index;
                    while (index < tag.Length && tag[index] != quote)
                    {
                        value += tag[index];
                        index++;
                    }

                    if (index < tag.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '>')
                    {
                        value += tag[index];
                        index++;
                    }
                }
            }

            if (name.Length > 0)
            {
                attributes.Add((name, StripRegions(value, text, regions, valueOffset), valueOffset));
            }
            else if (index < tag.Length)
            {
                index++;
            }
        }

        return attributes;
    }

    private static string StripRegions(string value, string text, IReadOnlyList<TemplateRegion> regions, int offset)
    {
        foreach (var region in regions)
        {
            if (region.Start < offset || region.Start > offset + value.Length)
            {
                continue;
            }

            var start = region.Start - offset;
            var length = Math.Min(region.Length, value.Length - start);
            if (start >= 0 && length > 0)
            {
                value = value.Remove(start, length);
            }
        }

        return value;
    }

    private static int FindTagEnd(string text, int start, IReadOnlyList<TemplateRegion> regions)
    {
        var index = start;
        var regionIndex = 0;
        while (index < text.Length)
        {
            if (regionIndex < regions.Count && regions[regionIndex].Start == index)
            {
                index = regions[regionIndex].Start + regions[regionIndex].Length;
                regionIndex++;
                continue;
            }

            if (text[index] == '>')
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool StartsWith(string text, int index, string value, bool ignoreCase = false) =>
        index + value.Length <= text.Length
        && string.Compare(text, index, value, 0, value.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;
}
