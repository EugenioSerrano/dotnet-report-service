using System.Text;
using System.Text.Encodings.Web;
using PaginationFixtures;

namespace NativeLayoutSpike;

/// <summary>
/// Builds one self-contained HTML document per run from a fixture: no script,
/// the stylesheet inline, every text value HTML-encoded. Header and page counter
/// come from native @page margin boxes; the bottom margin stays empty. The two
/// row markups follow the earlier layout spike (spikes/02-pdf-layout,
/// BlockHtmlBuilder.cs), with the caption rows of the fixture series.
/// </summary>
internal sealed class HtmlBuilder(string baseCss, string fontFamily)
{
    public const string Table = "table";
    public const string Grid = "grid";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public string Build(Fixture fixture, string markup)
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
            AppendTable(html, fixture);
        }
        else
        {
            AppendGrid(html, fixture);
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

    private static void AppendTable(StringBuilder html, Fixture fixture)
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
                    html.Append("<tr class=\"item\"><td class=\"seq\">").Append(row.Sequence).Append("</td><td class=\"date\">")
                        .Append(Encoder.Encode(row.Date!)).Append("</td><td class=\"desc\">").Append(Encoder.Encode(row.Description!)).Append("</td>");
                    if (five)
                    {
                        html.Append("<td class=\"note\">").Append(Encoder.Encode(row.Note ?? string.Empty)).Append("</td>");
                    }

                    html.Append("<td class=\"amount\">").Append(Encoder.Encode(row.Amount!)).Append("</td></tr>\n");
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

    private static void AppendGrid(StringBuilder html, Fixture fixture)
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
                    html.Append("<div class=\"item\"><div class=\"seq\">").Append(row.Sequence).Append("</div><div class=\"date\">")
                        .Append(Encoder.Encode(row.Date!)).Append("</div><div class=\"desc\">").Append(Encoder.Encode(row.Description!)).Append("</div>");
                    if (five)
                    {
                        html.Append("<div class=\"note\">").Append(Encoder.Encode(row.Note ?? string.Empty)).Append("</div>");
                    }

                    html.Append("<div class=\"amount\">").Append(Encoder.Encode(row.Amount!)).Append("</div></div>\n");
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
