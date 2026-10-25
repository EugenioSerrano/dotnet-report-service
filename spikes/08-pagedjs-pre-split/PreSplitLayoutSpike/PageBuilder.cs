using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PaginationFixtures;
using PdfLayoutSpike;

namespace PreSplitLayoutSpike;

/// <summary>
/// Builds the two pages of one run from a fixture, with one shared body:
/// <list type="bullet">
/// <item>semantic.html — the block as the HTML and Markdown outputs would see it: grid markup, no script, a bare body start tag;</item>
/// <item>pdf.html — the PDF-path page: the same body content, the page configuration, the pagination library,
/// the unchanged accounting script of spikes/02-pdf-layout and the pre-split script; its body start tag carries the footer switch.</item>
/// </list>
/// Every text value is HTML-encoded.
/// </summary>
internal sealed class PageBuilder(string layoutCss, string fixtureCss, string fontFamily)
{
    /// <summary>The script elements pdf.html holds (configuration, library, accounting script, pre-split script).</summary>
    public const int PdfScripts = 4;

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public (string Semantic, string Pdf) Build(Fixture fixture, bool footerOn)
    {
        var body = Body(fixture);
        var title = Encoder.Encode(fixture.Title);
        var semantic = new StringBuilder(body.Length + 8 * 1024);
        semantic.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(title).Append("</title>\n");
        semantic.Append("<style>\n").Append(layoutCss).Append("\n</style>\n");
        semantic.Append("<style>\n").Append(fixtureCss).Append("\n</style>\n");
        semantic.Append("</head>\n<body>").Append(body).Append("</body>\n</html>\n");

        var pdf = new StringBuilder(body.Length + 8 * 1024);
        pdf.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(title).Append("</title>\n");
        pdf.Append("<style>\n").Append(layoutCss).Append("\n</style>\n");
        pdf.Append("<style>\n").Append(ConfigurationCss(fixture.Title)).Append("</style>\n");
        pdf.Append("<style>\n").Append(fixtureCss).Append("\n</style>\n");
        pdf.Append("<script>window.PagedConfig = { auto: false };</script>\n");
        pdf.Append("<script src=\"assets/vendor/pagedjs-0.4.3/paged.polyfill.js\"></script>\n");
        pdf.Append("<script src=\"assets/paged-layout.js\"></script>\n");
        pdf.Append("<script src=\"assets/pre-split.js\"></script>\n");
        pdf.Append("</head>\n<body data-footer=\"").Append(footerOn ? "on" : "off").Append("\">").Append(body).Append("</body>\n</html>\n");
        return (semantic.ToString(), pdf.ToString());
    }

    /// <summary>
    /// The bytes between the end of the body start tag and the body end tag, and the start tag itself.
    /// Null when the document has no body start and end tag.
    /// </summary>
    public static (byte[] Inner, string StartTag)? BodyInner(byte[] document)
    {
        var text = Encoding.Latin1.GetString(document);
        var start = text.IndexOf("<body", StringComparison.Ordinal);
        var end = text.LastIndexOf("</body>", StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            return null;
        }

        var tagEnd = text.IndexOf('>', start);
        if (tagEnd < 0 || tagEnd > end)
        {
            return null;
        }

        return (document[(tagEnd + 1)..end], text[start..(tagEnd + 1)]);
    }

    /// <summary>The page configuration of spikes/02-pdf-layout (paged mode): size, margins, header, counter, empty footer boxes, font.</summary>
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

    /// <summary>A CSS string literal with backslash, double quote and line breaks escaped.</summary>
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
        var html = new StringBuilder(capacity: 64 * 1024);
        html.Append('\n');
        html.Append("<h1 class=\"title\">").Append(Encoder.Encode(fixture.Title)).Append("</h1>\n");
        AppendFreeText(html, "free-before", fixture.FreeTextBefore);
        var classes = "rows-grid" + (five ? " five-column" : string.Empty) + (fixture.WhiteSpace == Fixture.WhiteSpacePreLine ? " pre-line" : string.Empty);
        html.Append("<div class=\"").Append(classes).Append("\">\n");
        html.Append("<div class=\"grid-head\"><div class=\"seq\">Seq</div><div class=\"date\">Date</div><div class=\"desc\">Description</div>")
            .Append(five ? "<div class=\"note\">Note</div>" : string.Empty).Append("<div class=\"amount\">Amount</div></div>\n");
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    html.Append("<div class=\"group-start\">").Append(Encoder.Encode(row.CaptionText)).Append("</div>\n");
                    break;
                case RowKind.Item:
                    var sequence = row.Sequence!.Value.ToString(CultureInfo.InvariantCulture);
                    html.Append("<div class=\"item\" data-seq=\"").Append(sequence).Append("\" data-amount=\"").Append(Encoder.Encode(row.Amount!))
                        .Append("\"><div class=\"seq\">").Append(sequence).Append("</div><div class=\"date\">").Append(Encoder.Encode(row.Date!))
                        .Append("</div><div class=\"desc\">").Append(Encoder.Encode(row.Description!)).Append("</div>");
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

/// <summary>Maps a fixture to the block types of spikes/02-pdf-layout so its unchanged verifier can check the layout.</summary>
internal static class FixtureAdapter
{
    public static Block ToBlock(Fixture fixture)
    {
        var records = new List<PdfLayoutSpike.DatasetRecord>();
        var rows = new List<BlockRow>();
        string groupId = string.Empty;
        string groupName = string.Empty;
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    groupId = row.GroupId ?? string.Empty;
                    groupName = row.GroupName ?? string.Empty;
                    rows.Add(new BlockRow(BlockRowKind.GroupStart, null, groupId, groupName, 0m));
                    break;
                case RowKind.Item:
                    var record = new PdfLayoutSpike.DatasetRecord(
                        row.Sequence!.Value,
                        groupId,
                        groupName,
                        row.Date ?? string.Empty,
                        row.Description ?? string.Empty,
                        decimal.Parse(row.Amount!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
                    records.Add(record);
                    rows.Add(new BlockRow(BlockRowKind.Item, record, groupId, groupName, 0m));
                    break;
                case RowKind.GroupEnd:
                    rows.Add(new BlockRow(
                        BlockRowKind.GroupEnd,
                        null,
                        row.GroupId ?? groupId,
                        groupName,
                        decimal.Parse(row.Total!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
            }
        }

        return new Block(fixture.Id, records, rows);
    }
}
