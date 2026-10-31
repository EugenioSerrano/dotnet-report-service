using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PaginationFixtures;

namespace PreSplitLayoutSpike;

/// <summary>
/// Builds the two pages of one run in table markup, with one shared body, in the
/// shape of the grid page builder of spikes/08-pagedjs-pre-split:
/// <list type="bullet">
/// <item>semantic.html — the block as the HTML and Markdown outputs would see it: table markup, no script, a bare body start tag;</item>
/// <item>pdf.html — the PDF-path page: the same body content, the same page configuration, the pagination library and
/// the unchanged accounting script of spikes/02-pdf-layout, and the table copy of the pre-split script; its body start
/// tag carries the footer switch.</item>
/// </list>
/// The table follows the shared fixture series' table structure (spikes/07-native-tall-rows): a column group, a header
/// row in the table head, group rows spanning the columns, the item rows and the block-total row. Item rows carry the
/// row's sequence and amount as data attributes, which the accounting script reads. Every text value is HTML-encoded.
/// Nothing here sets a column width: the widths are those of the base stylesheet of spikes/02-pdf-layout.
/// </summary>
internal sealed class TablePageBuilder(string layoutCss, string tableCss, string fontFamily)
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public (string Semantic, string Pdf) Build(Fixture fixture, bool footerOn)
    {
        var body = Body(fixture);
        var title = Encoder.Encode(fixture.Title);
        var semantic = new StringBuilder(body.Length + 8 * 1024);
        semantic.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(title).Append("</title>\n");
        semantic.Append("<style>\n").Append(layoutCss).Append("\n</style>\n");
        semantic.Append("<style>\n").Append(tableCss).Append("\n</style>\n");
        semantic.Append("</head>\n<body>").Append(body).Append("</body>\n</html>\n");

        var pdf = new StringBuilder(body.Length + 8 * 1024);
        pdf.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(title).Append("</title>\n");
        pdf.Append("<style>\n").Append(layoutCss).Append("\n</style>\n");
        pdf.Append("<style>\n").Append(ConfigurationCss(fixture.Title)).Append("</style>\n");
        pdf.Append("<style>\n").Append(tableCss).Append("\n</style>\n");
        pdf.Append("<script>window.PagedConfig = { auto: false };</script>\n");
        pdf.Append("<script src=\"assets/vendor/pagedjs-0.4.3/paged.polyfill.js\"></script>\n");
        pdf.Append("<script src=\"assets/paged-layout.js\"></script>\n");
        pdf.Append("<script src=\"assets/pre-split-table.js\"></script>\n");
        pdf.Append("</head>\n<body data-footer=\"").Append(footerOn ? "on" : "off").Append("\">").Append(body).Append("</body>\n</html>\n");
        return (semantic.ToString(), pdf.ToString());
    }

    /// <summary>The page configuration of spikes/02-pdf-layout (paged mode), as the grid page builder writes it.</summary>
    private string ConfigurationCss(string title)
    {
        var font = CssString(fontFamily);
        var css = new StringBuilder();
        css.Append("@page {\n  size: A4 portrait;\n  margin: 15mm 12mm 18mm 12mm;\n");
        css.Append("  @top-left { content: ").Append(CssString(title)).Append("; font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        css.Append("  @top-right { content: \"Page \" counter(page) \" of \" counter(pages); font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        foreach (var position in new[] { "left", "center", "right" })
        {
            css.Append("  @bottom-").Append(position).Append(" { content: \"\"; font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        }

        css.Append("}\n");
        css.Append("body { font-family: ").Append(font).Append(", sans-serif; }\n");
        return css.ToString();
    }

    private static string CssString(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\A ", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private static string Body(Fixture fixture)
    {
        var five = fixture.HasNote;
        var span = five ? 4 : 3;
        var html = new StringBuilder(capacity: 64 * 1024);
        html.Append('\n');
        html.Append("<h1 class=\"title\">").Append(Encoder.Encode(fixture.Title)).Append("</h1>\n");
        AppendFreeText(html, "free-before", fixture.FreeTextBefore);
        var classes = "rows" + (five ? " five-column" : string.Empty) + (fixture.WhiteSpace == Fixture.WhiteSpacePreLine ? " pre-line" : string.Empty);
        html.Append("<table class=\"").Append(classes).Append("\">\n<colgroup><col class=\"c-seq\"><col class=\"c-date\"><col class=\"c-desc\">")
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
                    var sequence = row.Sequence!.Value.ToString(CultureInfo.InvariantCulture);
                    html.Append("<tr class=\"item\" data-seq=\"").Append(sequence).Append("\" data-amount=\"").Append(Encoder.Encode(row.Amount!))
                        .Append("\"><td class=\"seq\">").Append(sequence).Append("</td><td class=\"date\">").Append(Encoder.Encode(row.Date!))
                        .Append("</td><td class=\"desc\">").Append(Encoder.Encode(row.Description!)).Append("</td>");
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
        AppendFreeText(html, "free-after", fixture.FreeTextAfter);
        return html.ToString();
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
}
