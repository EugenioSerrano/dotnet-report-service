namespace ReportService.TemplateChecks.PdfConstraints;

/// <summary>The declarations an element's own style attribute carries, and where it was written.</summary>
/// <param name="File">The package-relative path of the part that carries the element.</param>
/// <param name="Line">The 1-based line of the attribute.</param>
/// <param name="Declarations">The declarations, the element being their subject.</param>
public sealed record DetailStyle(string File, int Line, IReadOnlyList<StyleDeclaration> Declarations);

/// <summary>
/// One element a row part emits: what identifies it, where it sits in the part, the
/// declarations its own style attribute carries, whether the page ever paints text
/// inside it, and whether a template expression kept it from carrying a literal
/// identity at all.
/// </summary>
/// <param name="Tag">The lower-cased tag.</param>
/// <param name="Classes">The literal class tokens, in the order written.</param>
/// <param name="Id">The literal id, or empty.</param>
/// <param name="Position">The element's 1-based position among its siblings.</param>
/// <param name="Parent">The index of its parent element, or none for a root element.</param>
/// <param name="Style">The declarations of its style attribute, or none when it carries no attribute.</param>
/// <param name="TextBearing">Whether the element's own content carries text or an expression.</param>
/// <param name="DynamicIdentity">Whether a class token or the id held a template expression, which identifies nothing.</param>
public sealed record DetailElement(
    string Tag,
    IReadOnlyList<string> Classes,
    string Id,
    int Position,
    int? Parent,
    DetailStyle? Style,
    bool TextBearing,
    bool DynamicIdentity);

/// <summary>
/// Reads the part a PDF body uses for its rows, as text, and yields the elements it
/// emits in document order: the tag, the literal class tokens and id, the sibling
/// position and the parent chain, the declarations of an element's own style
/// attribute with the line it was written on, and whether the element carries text
/// in its own content. A class token or an id that holds a template expression
/// identifies nothing and is recorded as dynamic; a non-rendered element's content
/// is never detail text. No HTML parser and no engine evaluation is involved: the
/// part is author text.
/// </summary>
public static class DetailMarkup
{
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>The elements the part emits, in document order.</summary>
    /// <param name="file">The package-relative path of the part.</param>
    /// <param name="text">The part's decoded text.</param>
    public static IReadOnlyList<DetailElement> Read(string file, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(text);

        var elements = new List<Mutable>();
        var open = new List<int>();
        var siblings = new Dictionary<int, int>();

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '<')
            {
                if (!char.IsWhiteSpace(text[index]) && open.Count > 0 && !InsideNonRendered(elements, open))
                {
                    // The content belongs to the element the text sits in directly: an
                    // ancestor that holds nothing but child elements carries no text of
                    // its own, and inheriting a value is what its children do.
                    elements[open[^1]].TextBearing = true;
                }

                continue;
            }

            if (Starts(text, index, "<!--"))
            {
                index = Skip(text, index + 4, "-->");
                continue;
            }

            if (Starts(text, index, "</"))
            {
                var close = text.IndexOf('>', index);
                if (close < 0)
                {
                    break;
                }

                if (open.Count > 0)
                {
                    open.RemoveAt(open.Count - 1);
                }

                index = close;
                continue;
            }

            if (index + 1 < text.Length && char.IsLetter(text[index + 1]))
            {
                index = ReadOpenTag(file, text, index, elements, open, siblings);
                continue;
            }
        }

        return [.. elements.Select(element => element.ToElement(file))];
    }

    private static int ReadOpenTag(
        string file,
        string text,
        int start,
        List<Mutable> elements,
        List<int> open,
        Dictionary<int, int> siblings)
    {
        var index = start + 1;
        var nameStart = index;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '-' or ':'))
        {
            index++;
        }

        var tag = text[nameStart..index].ToLowerInvariant();
        var attributes = new List<(string Name, string Value, int Index)>();
        var selfClosing = false;

        while (index < text.Length)
        {
            var character = text[index];
            if (character == '>')
            {
                break;
            }

            if (character == '/' && index + 1 < text.Length && text[index + 1] == '>')
            {
                selfClosing = true;
                index++;
                break;
            }

            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            var attributeStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('=' or '>' or '/'))
            {
                index++;
            }

            var name = text[attributeStart..index];
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index < text.Length && text[index] == '=')
            {
                index++;
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                if (index < text.Length && text[index] is '"' or '\'')
                {
                    var quote = text[index];
                    index++;
                    var valueStart = index;
                    while (index < text.Length && text[index] != quote)
                    {
                        index++;
                    }

                    attributes.Add((name, text[valueStart..index], attributeStart));
                    if (index < text.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    var valueStart = index;
                    while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '>')
                    {
                        index++;
                    }

                    attributes.Add((name, text[valueStart..index], attributeStart));
                }

                continue;
            }

            attributes.Add((name, string.Empty, attributeStart));
        }

        var parent = open.Count > 0 ? open[^1] : -1;
        var count = siblings.TryGetValue(parent, out var seen) ? seen + 1 : 1;
        siblings[parent] = count;

        var element = new Mutable
        {
            Tag = tag,
            Position = count,
            Parent = parent >= 0 ? parent : null,
            NonRendered = PdfConstraintRules.NonRenderedElements.Contains(tag),
        };

        foreach (var (name, value, attributeIndex) in attributes)
        {
            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = ClassTokens(value, out var dynamicClass);
                foreach (var token in tokens)
                {
                    element.Classes.Add(token);
                }

                element.DynamicIdentity |= dynamicClass;
            }
            else if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (IsDynamic(value))
                {
                    element.DynamicIdentity = true;
                }
                else
                {
                    element.Id = value.Trim();
                }
            }
            else if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                element.StyleLine = 1 + CountLines(text, attributeIndex);
                element.StyleText = value;
            }
        }

        var position = elements.Count;
        element.Index = position;
        elements.Add(element);
        if (!selfClosing && !Void.Contains(tag))
        {
            open.Add(position);
        }

        return index;
    }

    private static bool InsideNonRendered(List<Mutable> elements, List<int> open)
    {
        foreach (var position in open)
        {
            if (elements[position].NonRendered)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDynamic(string token) =>
        token.Contains("{{", StringComparison.Ordinal)
        || token.Contains("{%", StringComparison.Ordinal)
        || token.Contains("{#", StringComparison.Ordinal);

    private static List<string> ClassTokens(string value, out bool dynamic)
    {
        dynamic = false;
        var tokens = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                index++;
                continue;
            }

            if (Expression(value, index, out var end))
            {
                dynamic = true;
                index = end;
                continue;
            }

            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index]) && !Expression(value, index, out _))
            {
                index++;
            }

            if (index > start)
            {
                tokens.Add(value[start..index]);
            }
        }

        return tokens;
    }

    private static bool Expression(string text, int index, out int end)
    {
        end = index;
        if (index + 1 >= text.Length || text[index] != '{')
        {
            return false;
        }

        var closer = text[index + 1] switch
        {
            '{' => "}}",
            '%' => "%}",
            '#' => "#}",
            _ => null,
        };

        if (closer is null)
        {
            return false;
        }

        var found = text.IndexOf(closer, index + 2, StringComparison.Ordinal);
        end = found < 0 ? text.Length : found + closer.Length;
        return true;
    }

    private static bool Starts(string text, int index, string value) =>
        index + value.Length <= text.Length
        && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static int Skip(string text, int index, string terminator)
    {
        var found = text.IndexOf(terminator, index, StringComparison.Ordinal);
        return found < 0 ? text.Length : found + terminator.Length - 1;
    }

    private static string Identity(string tag, string id, IReadOnlyList<string> classes)
    {
        var builder = new System.Text.StringBuilder(tag);
        if (id.Length > 0)
        {
            _ = builder.Append('#').Append(id);
        }

        foreach (var token in classes)
        {
            _ = builder.Append('.').Append(token);
        }

        return builder.ToString();
    }

    private static int CountLines(string text, int upTo)
    {
        var lines = 0;
        for (var index = 0; index < upTo && index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private sealed class Mutable
    {
        public string Tag { get; init; } = string.Empty;

        public List<string> Classes { get; } = [];

        public string Id { get; set; } = string.Empty;

        public int Index { get; set; }

        public int Position { get; init; }

        public int? Parent { get; init; }

        public int StyleLine { get; set; }

        public string StyleText { get; set; } = string.Empty;

        public bool TextBearing { get; set; }

        public bool DynamicIdentity { get; set; }

        public bool NonRendered { get; init; }

        public DetailElement ToElement(string file)
        {
            DetailStyle? style = null;
            if (StyleLine > 0)
            {
                style = new DetailStyle(
                    file,
                    StyleLine,
                    StylesheetScan.ReadInline(
                        file,
                        StyleLine,
                        Identity(Tag, Id, Classes),
                        StyleText,
                        [Index]));
            }

            return new DetailElement(Tag, Classes, Id, Position, Parent, style, TextBearing, DynamicIdentity);
        }
    }
}
