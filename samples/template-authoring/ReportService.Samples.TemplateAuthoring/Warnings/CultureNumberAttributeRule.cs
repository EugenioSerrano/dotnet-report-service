using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring;

/// <summary>
/// A number written in a culture's form where a machine reads it: <c>height="41,82"</c> is not
/// a length, so the graphic is drawn wrong or not at all. The rule reads the composed preview
/// one fragment at a time - each fragment's own UTF-8 bytes, so the part and the record are
/// those of the bytes - and looks only at positions that hold a number: the scalar numeric
/// attributes, <c>points</c>, <c>viewBox</c> and the CSS lengths of a <c>style</c> attribute.
/// Text between tags and every other attribute are a reader's text and are never judged;
/// <c>d</c>, <c>transform</c> and <c>stroke-dasharray</c> use commas as separators and are
/// not judged either.
/// </summary>
public sealed partial class CultureNumberAttributeRule : IWarningRule
{
    /// <summary>The stable name of the trap.</summary>
    public const string Code = "culture-number-in-attribute";

    private static readonly HashSet<string> ScalarAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "width", "height", "dx", "dy",
        "opacity", "fill-opacity", "stroke-opacity", "stop-opacity", "stroke-width", "stroke-miterlimit",
        "font-size", "offset", "pathLength", "startOffset", "textLength",
    };

    private static readonly HashSet<string> CssLengthProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "width", "height", "top", "left", "right", "bottom", "font-size", "line-height", "stroke-width",
        "opacity", "flex-basis", "gap",
    };

    /// <inheritdoc />
    public IEnumerable<AuthoringWarning> Find(WarningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        foreach (var fragment in input.Composed.Fragments)
        {
            var text = input.Composed.TextOf(fragment);
            foreach (Match tag in Tag().Matches(text))
            {
                var element = tag.Groups["name"].Value;
                foreach (Match attribute in Attribute().Matches(tag.Groups["attributes"].Value))
                {
                    var name = attribute.Groups["name"].Value;
                    var raw = attribute.Groups["dq"].Success ? attribute.Groups["dq"].Value
                        : attribute.Groups["sq"].Success ? attribute.Groups["sq"].Value
                        : attribute.Groups["bare"].Value;
                    var value = WebUtility.HtmlDecode(raw);
                    if (IsCultureNumber(name, value))
                    {
                        yield return new AuthoringWarning(
                            Code,
                            new WarningOrder(1, string.Empty, fragment.Start + tag.Index, attribute.Index),
                            [
                                new("part", PartName(fragment.Kind)),
                                new("record", fragment.RecordOrdinal.ToString(CultureInfo.InvariantCulture)),
                                new("element", element),
                                new("attribute", name),
                                new("value", value),
                            ],
                            "write the number with num_invariant");
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="value"/> of attribute <paramref name="name"/> holds a culture-formatted number.</summary>
    /// <param name="name">The attribute's name.</param>
    /// <param name="value">The attribute's decoded value.</param>
    public static bool IsCultureNumber(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (ScalarAttributes.Contains(name))
        {
            return IsCultureScalar(value.Trim());
        }

        if (string.Equals(name, "points", StringComparison.OrdinalIgnoreCase))
        {
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(token => token.Count(character => character == ',') > 1);
        }

        if (string.Equals(name, "viewBox", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length == 4 && tokens.Any(token => DigitCommaDigit().IsMatch(token));
        }

        if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var declaration in value.Split(';'))
            {
                var colon = declaration.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var property = declaration[..colon].Trim();
                var isLength = CssLengthProperties.Contains(property)
                    || property.StartsWith("margin", StringComparison.OrdinalIgnoreCase)
                    || property.StartsWith("padding", StringComparison.OrdinalIgnoreCase);
                if (isLength && DigitCommaDigit().IsMatch(declaration[(colon + 1)..]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCultureScalar(string value) => CommaDecimal().IsMatch(value) || GroupedNumber().IsMatch(value);

    private static string PartName(FragmentKind kind) => kind switch
    {
        FragmentKind.DocumentStart => TemplatePartNames.Of(TemplatePart.DocumentStart),
        FragmentKind.GroupStart => TemplatePartNames.Of(TemplatePart.GroupStart),
        FragmentKind.Row => TemplatePartNames.Of(TemplatePart.Row),
        FragmentKind.GroupEnd => TemplatePartNames.Of(TemplatePart.GroupEnd),
        _ => TemplatePartNames.Of(TemplatePart.DocumentEnd),
    };

    [GeneratedRegex(@"<(?<name>[A-Za-z][A-Za-z0-9:_-]*)(?<attributes>(?:\s[^<>]*?)?)/?>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<name>[^\s=/>""']+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s""'>]+))", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"^-?\d{1,3}(?:\.\d{3})*,\d+(?:%|px|pt|mm|cm|in|em|rem)?$|^-?\d+,\d+(?:%|px|pt|mm|cm|in|em|rem)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommaDecimal();

    [GeneratedRegex(@"^-?\d{1,3}(?:,\d{3})+(?:\.\d+)?(?:%|px|pt|mm|cm|in|em|rem)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GroupedNumber();

    [GeneratedRegex(@"\d,\d", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigitCommaDigit();
}
