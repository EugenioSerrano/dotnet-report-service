using System.Text;
using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>
/// One text unit of a composed block: the text of one leaf box, in the order the
/// document holds it, with the fragment it came from, the record it belongs to and
/// whether the surface repeats it at the top or the bottom of a page.
/// </summary>
/// <param name="Text">The unit's text, with its character references decoded.</param>
/// <param name="Normalised">The unit's text through the single normalisation both sides of a check use.</param>
/// <param name="FragmentIndex">The position of the fragment it came from in the composed block.</param>
/// <param name="Kind">The kind of fragment it came from.</param>
/// <param name="RecordOrdinal">The record of its fragment, or 0 for a document or group part.</param>
/// <param name="IndexInFragment">Its position among the units of its own fragment.</param>
/// <param name="RepeatableHead">Whether it was composed inside a repeated head.</param>
/// <param name="RepeatableFoot">Whether it was composed inside a repeated foot.</param>
public sealed record TextUnit(
    string Text,
    string Normalised,
    int FragmentIndex,
    FragmentKind Kind,
    long RecordOrdinal,
    int IndexInFragment,
    bool RepeatableHead,
    bool RepeatableFoot);

/// <summary>The text units of a composed block, in the order the document holds them.</summary>
/// <param name="Units">The units, in document order; units whose normalised text is empty are absent.</param>
public sealed record ExtractedText(IReadOnlyList<TextUnit> Units)
{
    /// <summary>The units of one fragment, in order.</summary>
    /// <param name="fragmentIndex">The position of the fragment in the composed block.</param>
    public IReadOnlyList<TextUnit> OfFragment(int fragmentIndex) =>
        Units.Where(unit => unit.FragmentIndex == fragmentIndex).ToList();
}

/// <summary>
/// Extracts the text units of a composed block. A unit is the text between two
/// boundaries - the start or end of a block-level element - so a value split by an
/// inline element stays one unit, and a cell or a paragraph is a unit of its own.
/// The content of the document's head, of scripts, styles, templates, noscript
/// elements and comments is skipped, character references are decoded, and a unit
/// whose normalised text is empty is dropped: an empty box prints nothing and can
/// prove nothing.
/// </summary>
public sealed class TextUnitExtractor
{
    private static readonly HashSet<string> Boundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "body", "header", "footer", "main", "section", "article", "div", "p",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "dl", "dt", "dd",
        "table", "caption", "thead", "tbody", "tfoot", "tr", "th", "td", "pre",
        "blockquote", "figure", "figcaption", "br", "hr",
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "template", "noscript",
    };

    private static bool IsRawText(string name) =>
        string.Equals(name, "script", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "style", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the units of <paramref name="block"/>.</summary>
    /// <param name="block">The composed block and the map of its fragments.</param>
    public ExtractedText Extract(ComposedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        var units = new List<TextUnit>();
        for (var index = 0; index < block.Fragments.Count; index++)
        {
            var fragment = block.Fragments[index];
            var fragmentUnits = Scan(block.TextOf(fragment), index, fragment);
            units.AddRange(fragmentUnits);
        }

        return new ExtractedText(units);
    }

    private static List<TextUnit> Scan(string html, int fragmentIndex, ComposedFragment fragment)
    {
        var units = new List<TextUnit>();
        var text = new StringBuilder();
        var skipped = 0;
        var head = 0;
        var foot = 0;
        var indexInFragment = 0;

        void Flush()
        {
            if (text.Length == 0)
            {
                return;
            }

            var decoded = System.Net.WebUtility.HtmlDecode(text.ToString());
            text.Clear();
            var normalised = Normalisation.Normalise(decoded);
            if (normalised.Length == 0)
            {
                return;
            }

            units.Add(new TextUnit(
                decoded,
                normalised,
                fragmentIndex,
                fragment.Kind,
                fragment.RecordOrdinal,
                indexInFragment++,
                head > 0,
                foot > 0));
        }

        var position = 0;
        while (position < html.Length)
        {
            var open = html.IndexOf('<', position);
            if (open < 0)
            {
                if (skipped == 0)
                {
                    text.Append(html, position, html.Length - position);
                }

                break;
            }

            if (open > position && skipped == 0)
            {
                text.Append(html, position, open - position);
            }

            if (string.CompareOrdinal(html, open, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                position = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (string.CompareOrdinal(html, open, "<!", 0, 2) == 0)
            {
                var end = html.IndexOf('>', open);
                position = end < 0 ? html.Length : end + 1;
                continue;
            }

            var tag = ReadTag(html, open);
            if (tag is null)
            {
                // A '<' that opens no tag is text, as a browser treats it.
                if (skipped == 0)
                {
                    text.Append('<');
                }

                position = open + 1;
                continue;
            }

            if (tag.Value.Name.Length > 0)
            {
                var name = tag.Value.Name;
                if (Boundaries.Contains(name))
                {
                    Flush();
                }

                if (tag.Value.IsEnd)
                {
                    if (Skipped.Contains(name))
                    {
                        skipped = Math.Max(0, skipped - 1);
                    }
                    else if (string.Equals(name, "thead", StringComparison.OrdinalIgnoreCase))
                    {
                        head = Math.Max(0, head - 1);
                    }
                    else if (string.Equals(name, "tfoot", StringComparison.OrdinalIgnoreCase))
                    {
                        foot = Math.Max(0, foot - 1);
                    }
                }
                else if (!tag.Value.IsSelfClosing && IsRawText(name))
                {
                    // A raw-text element holds text no markup reader parses: everything up
                    // to its own closing tag is skipped, so a script's operators never
                    // become tags.
                    var closing = html.IndexOf("</" + name, tag.Value.End, StringComparison.OrdinalIgnoreCase);
                    var end = closing < 0 ? html.Length : html.IndexOf('>', closing);
                    position = end < 0 ? html.Length : end + 1;
                    continue;
                }
                else
                {
                    if (Skipped.Contains(name) && !tag.Value.IsSelfClosing)
                    {
                        skipped++;
                    }
                    else if (string.Equals(name, "thead", StringComparison.OrdinalIgnoreCase) && !tag.Value.IsSelfClosing)
                    {
                        head++;
                    }
                    else if (string.Equals(name, "tfoot", StringComparison.OrdinalIgnoreCase) && !tag.Value.IsSelfClosing)
                    {
                        foot++;
                    }
                }
            }

            position = tag.Value.End;
        }

        Flush();
        return units;
    }

    /// <summary>
    /// Reads the tag that opens at <paramref name="open"/>, respecting quoted
    /// attribute values so a '&gt;' inside an attribute does not end the tag.
    /// </summary>
    private static (string Name, bool IsEnd, bool IsSelfClosing, int End)? ReadTag(string html, int open)
    {
        var position = open + 1;
        var isEnd = position < html.Length && html[position] == '/';
        if (isEnd)
        {
            position++;
        }

        var nameStart = position;
        while (position < html.Length && !char.IsWhiteSpace(html[position]) && html[position] is not ('>' or '/'))
        {
            position++;
        }

        var name = html[nameStart..position];
        if (name.Length == 0 || !char.IsLetter(name[0]))
        {
            return null;
        }

        var quote = '\0';
        while (position < html.Length)
        {
            var character = html[position];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                var selfClosing = position > 0 && html[position - 1] == '/';
                return (name, isEnd, selfClosing, position + 1);
            }

            position++;
        }

        return (name, isEnd, false, html.Length);
    }
}
