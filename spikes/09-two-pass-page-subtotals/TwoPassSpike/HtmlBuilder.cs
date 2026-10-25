using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PaginationFixtures;

namespace TwoPassSpike;

/// <summary>
/// Builds one self-contained HTML document per run from a fixture: no script,
/// the stylesheet inline, every text value HTML-encoded. Header and page counter
/// come from native @page margin boxes; the bottom margin stays empty — it is the
/// area the second pass stamps. Copied from spikes/07-native-tall-rows
/// (HtmlBuilder.cs) and changed only to add the row markers and the two amount
/// strategies:
/// - markers are links with a fixed, unroutable address wrapped around text the
///   row prints anyway: "s" around the sequence number, "e" around the last word
///   of the description (around the date when the description has no word) and,
///   in strategy "cell", "a" around the amount. A marker adds no text;
/// - strategy "stamped" leaves the item amount cell empty (the second pass
///   prints the amount); strategy "cell" prints it in the cell, as before.
/// Group rows, captions and the block-total row carry no marker.
/// </summary>
internal sealed class HtmlBuilder(string baseCss, string fontFamily)
{
    public const string Table = "table";
    public const string Grid = "grid";
    public const string Stamped = "stamped";
    public const string Cell = "cell";

    /// <summary>The marker address of a row; the second pass reads it back with <see cref="MarkerReader"/>.</summary>
    public const string MarkerPrefix = "https://m.invalid/r/";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public static string MarkerAddress(int sequence, char kind) =>
        MarkerPrefix + sequence.ToString(CultureInfo.InvariantCulture) + "/" + kind;

    public string Build(Fixture fixture, string markup, string strategy)
    {
        var html = new StringBuilder(capacity: 64 * 1024);
        var classes = new List<string>();
        if (fixture.AmountAlign == Fixture.AlignBottom)
        {
            classes.Add("align-bottom");
        }

        if (fixture.WhiteSpace == Fixture.WhiteSpacePreLine)
        {
            classes.Add("pre-line");
        }

        if (fixture.HasNote)
        {
            classes.Add("five-column");
        }

        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<title>").Append(Encoder.Encode(fixture.Title)).Append("</title>\n");
        html.Append("<style>\n").Append(baseCss).Append("\n</style>\n");
        html.Append("<style>\n").Append(PageCss(fixture.Title)).Append("</style>\n");
        html.Append("</head>\n<body class=\"").Append(string.Join(' ', classes)).Append("\">\n");
        html.Append("<h1 class=\"title\">").Append(Encoder.Encode(fixture.Title)).Append("</h1>\n");
        AppendFreeText(html, "free-before", fixture.FreeTextBefore);
        if (markup == Table)
        {
            AppendTable(html, fixture, strategy);
        }
        else
        {
            AppendGrid(html, fixture, strategy);
        }

        AppendFreeText(html, "free-after", fixture.FreeTextAfter);
        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private string PageCss(string title)
    {
        var font = CssString(fontFamily);
        var css = new StringBuilder();
        css.Append("@page {\n  size: A4 portrait;\n  margin: 15mm 12mm 18mm 12mm;\n");
        css.Append("  @top-left { content: ").Append(CssString(title)).Append("; font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        css.Append("  @top-right { content: \"Page \" counter(page) \" of \" counter(pages); font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        css.Append("}\n");
        css.Append("body { font-family: ").Append(font).Append(", sans-serif; }\n");
        return css.ToString();
    }

    /// <summary>A CSS string literal with backslash, double quote and line breaks escaped.</summary>
    private static string CssString(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\A ", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private static void AppendFreeText(StringBuilder html, string region, IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count == 0)
        {
            return;
        }

        html.Append("<div class=\"free ").Append(region).Append("\">\n");
        foreach (var paragraph in paragraphs)
        {
            html.Append("<p>").Append(Encoder.Encode(paragraph)).Append("</p>\n");
        }

        html.Append("</div>\n");
    }

    /// <summary>A marker link around already-encoded content; the address holds only an integer and a fixed letter.</summary>
    private static StringBuilder AppendMarker(StringBuilder html, int sequence, char kind, string encodedContent) =>
        html.Append("<a class=\"mk\" href=\"").Append(MarkerAddress(sequence, kind)).Append("\">").Append(encodedContent).Append("</a>");

    /// <summary>The sequence number wrapped in its start marker.</summary>
    private static void AppendSequence(StringBuilder html, int sequence) =>
        AppendMarker(html, sequence, 's', sequence.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The description with its last word wrapped in the end marker. The text is
    /// split at the last whitespace run, so the printed characters and the break
    /// opportunities are those of the plain description. Returns false when the
    /// description has no word (the caller then wraps the date).
    /// </summary>
    private static bool AppendDescription(StringBuilder html, int sequence, string description)
    {
        var end = description.Length;
        while (end > 0 && char.IsWhiteSpace(description[end - 1]))
        {
            end--;
        }

        if (end == 0)
        {
            html.Append(Encoder.Encode(description));
            return false;
        }

        var start = end;
        while (start > 0 && !char.IsWhiteSpace(description[start - 1]))
        {
            start--;
        }

        html.Append(Encoder.Encode(description[..start]));
        AppendMarker(html, sequence, 'e', Encoder.Encode(description[start..end]));
        html.Append(Encoder.Encode(description[end..]));
        return true;
    }

    /// <summary>The date, wrapped in the end marker when the description has no word.</summary>
    private static void AppendDate(StringBuilder html, int sequence, string date, bool descriptionHasWord)
    {
        if (descriptionHasWord)
        {
            html.Append(Encoder.Encode(date));
        }
        else
        {
            AppendMarker(html, sequence, 'e', Encoder.Encode(date));
        }
    }

    /// <summary>Strategy "cell": the amount wrapped in its marker; strategy "stamped": nothing (the cell stays empty).</summary>
    private static void AppendAmount(StringBuilder html, int sequence, string amount, string strategy)
    {
        if (strategy == Cell)
        {
            AppendMarker(html, sequence, 'a', Encoder.Encode(amount));
        }
    }

    private static void AppendItemCells(StringBuilder html, FixtureRow row, bool five, string strategy, string tag)
    {
        var sequence = row.Sequence!.Value;
        var description = new StringBuilder();
        var hasWord = AppendDescription(description, sequence, row.Description!);
        html.Append('<').Append(tag).Append(" class=\"seq\">");
        AppendSequence(html, sequence);
        html.Append("</").Append(tag).Append("><").Append(tag).Append(" class=\"date\">");
        AppendDate(html, sequence, row.Date!, hasWord);
        html.Append("</").Append(tag).Append("><").Append(tag).Append(" class=\"desc\">").Append(description).Append("</").Append(tag).Append('>');
        if (five)
        {
            html.Append('<').Append(tag).Append(" class=\"note\">").Append(Encoder.Encode(row.Note ?? string.Empty)).Append("</").Append(tag).Append('>');
        }

        html.Append('<').Append(tag).Append(" class=\"amount\">");
        AppendAmount(html, sequence, row.Amount!, strategy);
        html.Append("</").Append(tag).Append('>');
    }

    private static void AppendTable(StringBuilder html, Fixture fixture, string strategy)
    {
        var five = fixture.HasNote;
        var span = five ? 4 : 3;
        html.Append("<table class=\"rows\">\n<colgroup><col class=\"c-seq\"><col class=\"c-date\"><col class=\"c-desc\">")
            .Append(five ? "<col class=\"c-note\">" : string.Empty).Append("<col class=\"c-amount\"></colgroup>\n");
        html.Append("<thead><tr><th class=\"seq\">Seq</th><th class=\"date\">Date</th><th class=\"desc\">Description</th>")
            .Append(five ? "<th class=\"note\">Note</th>" : string.Empty).Append("<th class=\"amount\">Amount</th></tr></thead>\n<tbody>\n");
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    html.Append("<tr class=\"group-start\"><td colspan=\"").Append(span + 1).Append("\">").Append(Encoder.Encode(row.CaptionText)).Append("</td></tr>\n");
                    break;
                case RowKind.Item:
                    html.Append("<tr class=\"item\">");
                    AppendItemCells(html, row, five, strategy, "td");
                    html.Append("</tr>\n");
                    break;
                case RowKind.GroupEnd:
                    html.Append("<tr class=\"group-end\"><td colspan=\"").Append(span).Append("\">").Append(Captions.GroupTotal)
                        .Append("</td><td class=\"amount\">").Append(Encoder.Encode(row.Total!)).Append("</td></tr>\n");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
            }
        }

        html.Append("<tr class=\"block-total\"><td colspan=\"").Append(span).Append("\">").Append(Captions.BlockTotal)
            .Append("</td><td class=\"amount\">").Append(Encoder.Encode(fixture.BlockTotal)).Append("</td></tr>\n");
        html.Append("</tbody>\n</table>\n");
    }

    private static void AppendGrid(StringBuilder html, Fixture fixture, string strategy)
    {
        var five = fixture.HasNote;
        html.Append("<div class=\"rows-grid\">\n<div class=\"grid-head\"><div class=\"seq\">Seq</div><div class=\"date\">Date</div><div class=\"desc\">Description</div>")
            .Append(five ? "<div class=\"note\">Note</div>" : string.Empty).Append("<div class=\"amount\">Amount</div></div>\n");
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    html.Append("<div class=\"group-start\">").Append(Encoder.Encode(row.CaptionText)).Append("</div>\n");
                    break;
                case RowKind.Item:
                    html.Append("<div class=\"item\">");
                    AppendItemCells(html, row, five, strategy, "div");
                    html.Append("</div>\n");
                    break;
                case RowKind.GroupEnd:
                    html.Append("<div class=\"group-end\"><div class=\"label\">").Append(Captions.GroupTotal).Append("</div><div class=\"amount\">")
                        .Append(Encoder.Encode(row.Total!)).Append("</div></div>\n");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
            }
        }

        html.Append("<div class=\"block-total\"><div class=\"label\">").Append(Captions.BlockTotal).Append("</div><div class=\"amount\">")
            .Append(Encoder.Encode(fixture.BlockTotal)).Append("</div></div>\n");
        html.Append("</div>\n");
    }
}
