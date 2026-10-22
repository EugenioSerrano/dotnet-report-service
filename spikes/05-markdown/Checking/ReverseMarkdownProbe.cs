using System.Text;
using MarkdownSpike.Rendering;
using MarkdownSpike.Routes;
using ReverseMarkdown;

namespace MarkdownSpike.Checking;

/// <summary>
/// Converts fixed fragments with the library configuration of the library
/// routes and prints input and output, so the stitching rules rest on observed
/// behaviour. Saved as results/probe.txt.
/// </summary>
internal static class ReverseMarkdownProbe
{
    private static readonly (string Name, string Html)[] Probes =
    [
        ("a: a table row alone", "<tr><td>1</td><td>x</td></tr>"),
        ("b: a full table with a header", "<table><thead><tr><th>#</th><th>Name</th></tr></thead><tbody><tr><td>1</td><td>a</td></tr><tr><td>2</td><td>b</td></tr></tbody></table>"),
        ("c: a table without a header", "<table><tbody><tr><td>1</td><td>a</td></tr><tr><td>2</td><td>b</td></tr></tbody></table>"),
        ("d: cells with special characters, long and non-ASCII text", "<table><thead><tr><th>Text</th><th>Other</th></tr></thead><tbody><tr><td>a|b &lt;b&gt;bold&lt;/b&gt; &amp;amp; * _ [x] \\ `</td><td>Tom &amp; Jerry &quot;quoted&quot; &lt;script&gt;alert(1)&lt;/script&gt;</td></tr><tr><td>" + new string('x', 2000) + "</td><td>reparación tensión € ñ</td></tr></tbody></table>"),
        ("e: a table foot row", "<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody><tfoot><tr><td>Total</td><td>2</td></tr></tfoot></table>"),
        ("f: group sections with a spanning heading and a spanning subtotal (the grouped package's shape)", "<table class=\"items\"><thead><tr><th class=\"num\">#</th><th>Group</th><th>Amount</th></tr></thead><tbody class=\"group\"><tr class=\"group-heading\"><th colspan=\"3\">G0001 East plant</th></tr><tr><td class=\"num\">1</td><td>G0001</td><td class=\"num\">10.00</td></tr><tr class=\"group-subtotal\"><td colspan=\"2\">Subtotal G0001</td><td class=\"num\">10.00</td></tr></tbody><tbody class=\"group\"><tr class=\"group-heading\"><th colspan=\"3\">G0002 West</th></tr></tbody></table>"),
        ("g: the document start of the grouped package (head, header, an open table)", "<!DOCTYPE html>\n<html lang=\"es-AR\">\n<head>\n  <meta charset=\"utf-8\">\n  <title>Report &amp; title</title>\n  <link rel=\"stylesheet\" href=\"styles.css\">\n</head>\n<body>\n<header class=\"document-header\">\n  <h1>Report &amp; title</h1>\n  <p class=\"customer\">Demo (C1)</p>\n</header>\n<table class=\"items\">\n  <thead>\n    <tr><th>#</th><th>Name</th></tr>\n  </thead>\n</table></body></html>"),
        ("h: template-like text in a cell and a closing section", "<table><thead><tr><th>A</th></tr></thead><tbody><tr><td>{{ header.customer.name }} ${7*7} {% raw %}{{ 7*7 }}{% endraw %} '; DROP TABLE items; --</td></tr></tbody></table><section class=\"document-totals\"><p>Records: 3</p><p>Total: 1000.00 ARS</p></section>"),
    ];

    public static int Run(CommandLine line)
    {
        line.Allow();
        string report = Report();
        Directory.CreateDirectory(RepositoryPaths.Results);
        File.WriteAllText(Path.Combine(RepositoryPaths.Results, "probe.txt"), report, new UTF8Encoding(false));
        Console.Write(report);
        return 0;
    }

    public static string Report()
    {
        var converter = new Converter(FragmentStitchingConverter.CreateConfig());
        var report = new StringBuilder();
        report.Append("Library: ReverseMarkdown ").Append(typeof(Converter).Assembly.GetName().Version).Append('\n');
        report.Append("Configuration: Flavor=Default (GitHub-compatible pipe tables), Tags.Unknown=Drop, Formatting.RemoveComments=true, Formatting.OutputLineEnding=LF, Tables.WithoutHeaderRow=EmptyRow\n\n");
        foreach ((string name, string html) in Probes)
        {
            Append(report, name, html, converter.Convert(html));
        }

        var flavor = new Config { Flavor = Config.MarkdownFlavor.GitHub };
        Append(report, "i: probe b with Flavor=GitHub (the documented GitHub value, not used)", Probes[1].Html, new Converter(flavor).Convert(Probes[1].Html));

        var output = new StringWriter { NewLine = "\n" };
        var stitching = new FragmentStitchingConverter(output);
        const string first = "<table><thead><tr><th>#</th><th>Name</th></tr></thead><tbody><tr><td>1</td><td>a</td></tr>";
        const string second = "<tr><td>2</td><td>b</td></tr></tbody></table>";
        stitching.OnFragment(first, FragmentKind.Block);
        stitching.OnFragment(second, FragmentKind.Block);
        stitching.Complete();
        Append(report, "j: probe b split after its first body row, converted per fragment and stitched", first + "  ||  " + second, output.ToString());
        return report.ToString();
    }

    private static void Append(StringBuilder report, string name, string html, string markdown)
    {
        report.Append("== ").Append(name).Append('\n');
        report.Append("-- input\n").Append(Shorten(html)).Append('\n');
        report.Append("-- output\n").Append(Shorten(markdown.Replace("\r\n", "\n", StringComparison.Ordinal))).Append("\n\n");
    }

    private static string Shorten(string text) =>
        text.Replace(new string('x', 2000), "x…(2 000 characters)…x", StringComparison.Ordinal);
}
