using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace PdfLayoutSpike;

internal enum LayoutMode
{
    Paged,
    Native,
}

internal enum RowMarkup
{
    Table,
    Grid,
}

internal enum KeepPolicy
{
    Split,
    Keep,
}

/// <summary>How a block is laid out: pagination library or native printing, row markup and keep policy.</summary>
internal sealed record RunConfiguration(LayoutMode Mode, RowMarkup Markup, KeepPolicy Keep)
{
    public string Name => Mode == LayoutMode.Native
        ? $"native-{Markup.ToString().ToLowerInvariant()}"
        : $"paged-{Markup.ToString().ToLowerInvariant()}-{Keep.ToString().ToLowerInvariant()}";
}

/// <summary>Writes one self-contained HTML document per run. Every text value is HTML-encoded.</summary>
internal sealed class BlockHtmlBuilder(string baseCss, string fontFamily)
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    /// <summary>Builds the document and returns it with the number of script elements it contains.</summary>
    public (string Html, int ScriptCount) Build(BlockSource source, Block block, RunConfiguration config, bool footer, string runId)
    {
        var html = new StringBuilder(capacity: 256 * 1024);
        var scripts = 0;
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append("<title>").Append(Encoder.Encode(source.ReportTitle)).Append("</title>\n");
        html.Append("<style>\n").Append(baseCss).Append("\n</style>\n");
        html.Append("<style>\n").Append(ConfigurationCss(source.ReportTitle, config)).Append("</style>\n");
        if (config.Mode == LayoutMode.Paged)
        {
            html.Append("<script>window.PagedConfig = { auto: false };</script>\n");
            html.Append("<script src=\"assets/vendor/pagedjs-0.4.3/paged.polyfill.js\"></script>\n");
            html.Append("<script src=\"assets/paged-layout.js\"></script>\n");
            scripts = 3;
        }

        html.Append("</head>\n<body data-run=\"").Append(Encoder.Encode(runId))
            .Append("\" data-footer=\"").Append(footer ? "on" : "off").Append("\">\n");
        html.Append("<div class=\"title\"><h1>").Append(Encoder.Encode(source.ReportTitle)).Append("</h1><p>")
            .Append(Encoder.Encode(source.CustomerName)).Append(" — block ").Append(Encoder.Encode(block.Name))
            .Append("</p></div>\n");

        if (config.Markup == RowMarkup.Table)
        {
            AppendTable(html, block);
        }
        else
        {
            AppendGrid(html, block);
        }

        html.Append("</body>\n</html>\n");
        return (html.ToString(), scripts);
    }

    /// <summary>The probe document of the native margin-box check: three pages, a header and a page counter.</summary>
    public string BuildNativeProbe()
    {
        var font = CssString(fontFamily);
        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Native margin box probe</title>\n<style>\n"
            + "@page {\n  size: A4 portrait;\n  margin: 20mm 15mm;\n"
            + $"  @top-center {{ content: \"Native margin box probe\"; font-family: {font}; font-size: 10pt; }}\n"
            + $"  @bottom-right {{ content: \"Page \" counter(page) \" of \" counter(pages); font-family: {font}; font-size: 10pt; }}\n}}\n"
            + $"body {{ font-family: {font}; }}\n.p {{ break-after: page; }}\n.p:last-child {{ break-after: auto; }}\n</style>\n</head>\n<body>\n"
            + "<div class=\"p\"><h1>First page</h1></div>\n<div class=\"p\"><h1>Second page</h1></div>\n<div class=\"p\"><h1>Third page</h1></div>\n"
            + "</body>\n</html>\n";
    }

    private string ConfigurationCss(string title, RunConfiguration config)
    {
        var font = CssString(fontFamily);
        var css = new StringBuilder();
        css.Append("@page {\n  size: A4 portrait;\n  margin: 15mm 12mm 18mm 12mm;\n");
        css.Append("  @top-left { content: ").Append(CssString(title)).Append("; font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        css.Append("  @top-right { content: \"Page \" counter(page) \" of \" counter(pages); font-family: ").Append(font).Append("; font-size: 8pt; }\n");
        if (config.Mode == LayoutMode.Paged)
        {
            foreach (var position in new[] { "left", "center", "right" })
            {
                css.Append("  @bottom-").Append(position).Append(" { content: \"\"; font-family: ").Append(font).Append("; font-size: 8pt; }\n");
            }
        }

        css.Append("}\n");
        css.Append("body { font-family: ").Append(font).Append(", sans-serif; }\n");
        if (config.Keep == KeepPolicy.Keep)
        {
            css.Append(".item { break-inside: avoid; }\n");
        }

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

    private static void AppendTable(StringBuilder html, Block block)
    {
        html.Append("<table class=\"rows\">\n<colgroup><col class=\"c-seq\"><col class=\"c-date\"><col class=\"c-desc\"><col class=\"c-amount\"></colgroup>\n");
        html.Append("<thead><tr><th>#</th><th>Date</th><th>Description</th><th class=\"amount\">Amount</th></tr></thead>\n<tbody>\n");
        foreach (var row in block.Rows)
        {
            switch (row.Kind)
            {
                case BlockRowKind.GroupStart:
                    html.Append("<tr class=\"group-start\" data-group=\"").Append(Encoder.Encode(row.GroupId)).Append("\"><td colspan=\"4\">")
                        .Append(Encoder.Encode(row.GroupId)).Append(" — ").Append(Encoder.Encode(row.GroupName)).Append("</td></tr>\n");
                    break;
                case BlockRowKind.Item:
                    var r = row.Record!;
                    html.Append("<tr class=\"item\" data-seq=\"").Append(r.Sequence.ToString(CultureInfo.InvariantCulture))
                        .Append("\" data-amount=\"").Append(r.AmountText).Append("\"><td class=\"seq\">")
                        .Append(r.Sequence.ToString(CultureInfo.InvariantCulture)).Append("</td><td class=\"date\">")
                        .Append(Encoder.Encode(r.Date)).Append("</td><td class=\"desc\">").Append(Encoder.Encode(r.Description))
                        .Append("</td><td class=\"amount\">").Append(r.AmountText).Append("</td></tr>\n");
                    break;
                case BlockRowKind.GroupEnd:
                    html.Append("<tr class=\"group-end\" data-group=\"").Append(Encoder.Encode(row.GroupId))
                        .Append("\"><td colspan=\"3\">Group total</td><td class=\"amount\">")
                        .Append(row.GroupTotal.ToString("0.00", CultureInfo.InvariantCulture)).Append("</td></tr>\n");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
            }
        }

        html.Append("</tbody>\n</table>\n");
    }

    private static void AppendGrid(StringBuilder html, Block block)
    {
        html.Append("<div class=\"rows-grid\">\n<div class=\"grid-head\"><div class=\"seq\">#</div><div class=\"date\">Date</div><div class=\"desc\">Description</div><div class=\"amount\">Amount</div></div>\n");
        foreach (var row in block.Rows)
        {
            switch (row.Kind)
            {
                case BlockRowKind.GroupStart:
                    html.Append("<div class=\"group-start\" data-group=\"").Append(Encoder.Encode(row.GroupId)).Append("\">")
                        .Append(Encoder.Encode(row.GroupId)).Append(" — ").Append(Encoder.Encode(row.GroupName)).Append("</div>\n");
                    break;
                case BlockRowKind.Item:
                    var r = row.Record!;
                    html.Append("<div class=\"item\" data-seq=\"").Append(r.Sequence.ToString(CultureInfo.InvariantCulture))
                        .Append("\" data-amount=\"").Append(r.AmountText).Append("\"><div class=\"seq\">")
                        .Append(r.Sequence.ToString(CultureInfo.InvariantCulture)).Append("</div><div class=\"date\">")
                        .Append(Encoder.Encode(r.Date)).Append("</div><div class=\"desc\">").Append(Encoder.Encode(r.Description))
                        .Append("</div><div class=\"amount\">").Append(r.AmountText).Append("</div></div>\n");
                    break;
                case BlockRowKind.GroupEnd:
                    html.Append("<div class=\"group-end\" data-group=\"").Append(Encoder.Encode(row.GroupId))
                        .Append("\"><div class=\"label\">Group total</div><div class=\"amount\">")
                        .Append(row.GroupTotal.ToString("0.00", CultureInfo.InvariantCulture)).Append("</div></div>\n");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown row kind {row.Kind}.");
            }
        }

        html.Append("</div>\n");
    }
}
