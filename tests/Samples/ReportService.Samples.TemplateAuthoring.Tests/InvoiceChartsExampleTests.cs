using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Rendering;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The charts example: each invoice draws monthly bars with a trend line and an average
/// line, and a donut of its subtotal, computing the geometry in the template from the
/// plain numbers of its record. Every expected coordinate is computed here, in
/// <see cref="decimal"/>, from the package's own <c>sample.json</c> and the drawing
/// constants below — never read from the composed document. The example is rendered as
/// shipped (<c>es-AR</c>, comma decimals) and as a copy whose only change is the culture
/// <c>en-US</c>; each golden replays the live print of that culture.
/// </summary>
public sealed class InvoiceChartsExampleTests
{
    private const string Id = "invoice-charts";

    private const string EnglishGolden = "invoice-charts-en-US";

    private const string InvoiceOpening = "<section class=\"invoice\">";

    // The drawing constants the row part writes as literals.
    private const decimal Baseline = 110m;

    private const decimal PlotHeight = 100.0m;

    private const int BarStep = 30;

    private const int BarOffset = 4;

    private const int BarWidth = 22;

    private const decimal Circumference = 376.99m;

    private const decimal StartOffset = 94.25m;

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] SingleNumberAttributes =
        ["x", "y", "width", "height", "x1", "y1", "x2", "y2", "cx", "cy", "r", "stroke-dashoffset"];

    /// <summary>
    /// The example as shipped (es-AR) and under en-US renders six pages, the product's
    /// verification accepts the printed page, and no authoring warning is printed.
    /// </summary>
    /// <param name="english">Whether the copy with the culture en-US is rendered.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheChartsExampleRendersAndVerifiesInBothCulturesWithoutWarnings(bool english)
    {
        var (code, output, html, printer) = await RenderAsync(english);

        Assert.Equal(0, code);
        Assert.Contains("rendered " + Id + " 1.0.0: engine=liquid pages=6 ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", output, StringComparison.Ordinal);
        Assert.Equal(printer.Document, html);
    }

    /// <summary>Every number written into a chart's geometry is invariant text: a point decimal, no group separator.</summary>
    /// <param name="english">Whether the copy with the culture en-US is rendered.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryGeometryAttributeIsInvariantNumberText(bool english)
    {
        var (_, _, html, _) = await RenderAsync(english);
        var svgs = ChartSvgs(html);
        Assert.Equal(12, svgs.Count);

        var checkedValues = 0;
        foreach (var svg in svgs)
        {
            foreach (Match element in Regex.Matches(svg, "<(?:rect|line|circle|polyline)\\b[^>]*>", RegexOptions.None, RegexTimeout))
            {
                foreach (Match attribute in Regex.Matches(element.Value, "\\s([a-z0-9-]+)=\"([^\"]*)\"", RegexOptions.None, RegexTimeout))
                {
                    var name = attribute.Groups[1].Value;
                    var value = attribute.Groups[2].Value;
                    if (SingleNumberAttributes.Contains(name))
                    {
                        AssertInvariantNumber(name, value);
                        checkedValues++;
                    }
                    else if (name is "points" or "stroke-dasharray")
                    {
                        foreach (var part in value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                        {
                            AssertInvariantNumber(name, part);
                            checkedValues++;
                        }
                    }
                }
            }
        }

        Assert.True(checkedValues > 500, "Too few geometry values were found: " + checkedValues.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The charts' geometry is the same in both cultures, while the business values the
    /// invoice prints are formatted in each culture.
    /// </summary>
    [Fact]
    public async Task TheGeometryIsTheSameInBothCulturesWhileBusinessValuesFollowTheCulture()
    {
        var (_, _, spanishHtml, _) = await RenderAsync(english: false);
        var (_, _, englishHtml, _) = await RenderAsync(english: true);

        Assert.Equal(ChartSvgs(spanishHtml), ChartSvgs(englishHtml));

        var first = Invoices()[0];
        var total = first.GetProperty("total").GetDecimal();
        Assert.Contains("Total " + total.ToString("N2", Spanish), Decode(spanishHtml), StringComparison.Ordinal);
        Assert.Contains("Total " + total.ToString("N2", English), Decode(englishHtml), StringComparison.Ordinal);
        Assert.NotEqual(total.ToString("N2", Spanish), total.ToString("N2", English));
    }

    /// <summary>
    /// Each bar's position and height, and each trend-line point, are the series scaled to
    /// its maximum; a month at zero is a bar of height 0 and the tallest month fills the plot.
    /// </summary>
    [Fact]
    public async Task TheBarsAndTheTrendLineAreTheSeriesScaledToItsMaximum()
    {
        var (_, _, html, _) = await RenderAsync(english: false);
        var sections = Sections(html);
        var invoices = Invoices();
        Assert.Equal(invoices.Count, sections.Count);

        var sawZeroMonth = false;
        for (var index = 0; index < invoices.Count; index++)
        {
            var series = Series(invoices[index]);
            var max = series.Max();
            var bars = Regex.Matches(
                sections[index],
                "<rect class=\"bar\" x=\"([^\"]*)\" y=\"([^\"]*)\" width=\"([^\"]*)\" height=\"([^\"]*)\">",
                RegexOptions.None,
                RegexTimeout);
            Assert.Equal(series.Count, bars.Count);

            var points = new List<string>();
            for (var month = 0; month < series.Count; month++)
            {
                var height = max > 0 ? series[month] * PlotHeight / max : 0m;
                var x = (month * BarStep) + BarOffset;
                var y = Text(Baseline - height);
                Assert.Equal(x.ToString(CultureInfo.InvariantCulture), bars[month].Groups[1].Value);
                Assert.Equal(y, bars[month].Groups[2].Value);
                Assert.Equal(BarWidth.ToString(CultureInfo.InvariantCulture), bars[month].Groups[3].Value);
                Assert.Equal(Text(height), bars[month].Groups[4].Value);
                points.Add((x + (BarWidth / 2)).ToString(CultureInfo.InvariantCulture) + "," + y);
                if (series[month] == 0m && max > 0)
                {
                    Assert.Equal("0.00", bars[month].Groups[4].Value);
                    sawZeroMonth = true;
                }

                if (max > 0 && series[month] == max)
                {
                    Assert.Equal("100.00", bars[month].Groups[4].Value);
                }
            }

            Assert.Contains("<polyline class=\"trend\" points=\"" + string.Join(' ', points) + "\">", sections[index], StringComparison.Ordinal);
        }

        Assert.True(sawZeroMonth, "The sample must carry a month at zero.");
    }

    /// <summary>
    /// The average line is drawn at the record's own average, scaled like the bars, and
    /// its caption prints that same value formatted in the job's culture — never an
    /// average the template computed.
    /// </summary>
    [Fact]
    public async Task TheAverageLineAndItsCaptionUseTheRecordsOwnAverage()
    {
        var (_, _, html, _) = await RenderAsync(english: false);
        var sections = Sections(html);
        var invoices = Invoices();

        for (var index = 0; index < invoices.Count; index++)
        {
            var average = invoices[index].GetProperty("consumptionAverage").GetDecimal();
            var max = Series(invoices[index]).Max();
            var y = Text(Baseline - (max > 0 ? average * PlotHeight / max : 0m));

            Assert.Contains(
                "<line class=\"average\" x1=\"0\" y1=\"" + y + "\" x2=\"370\" y2=\"" + y + "\">",
                sections[index],
                StringComparison.Ordinal);
            Assert.Contains("promedio " + average.ToString("N1", Spanish) + "<", Decode(sections[index]), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Each donut slice is its line's share of the subtotal on the ring's circumference, the
    /// slices follow one another, a single line is the whole ring, and the legend prints each
    /// share as a percentage in the job's culture.
    /// </summary>
    [Fact]
    public async Task TheDonutSlicesAreEachLinesShareOfTheSubtotal()
    {
        var (_, _, html, _) = await RenderAsync(english: false);
        var sections = Sections(html);
        var invoices = Invoices();
        var sawSingleLine = false;

        for (var index = 0; index < invoices.Count; index++)
        {
            var invoice = invoices[index];
            var subtotal = invoice.GetProperty("subtotal").GetDecimal();
            var lines = invoice.GetProperty("lines").EnumerateArray().ToList();
            var slices = Regex.Matches(
                sections[index],
                "<circle class=\"slice\"[^>]*stroke-dasharray=\"([^\"]*)\" stroke-dashoffset=\"([^\"]*)\">",
                RegexOptions.None,
                RegexTimeout);
            Assert.Equal(lines.Count, slices.Count);

            var drawn = 0m;
            for (var line = 0; line < lines.Count; line++)
            {
                var amount = lines[line].GetProperty("amount").GetDecimal();
                var slice = amount * Circumference / subtotal;
                Assert.Equal(Text(slice) + " " + Text(Circumference - slice), slices[line].Groups[1].Value);
                Assert.Equal(Text(StartOffset - drawn), slices[line].Groups[2].Value);
                drawn += slice;

                var share = amount * 100.0m / subtotal;
                Assert.Contains(
                    lines[line].GetProperty("description").GetString() + " · " + share.ToString("N1", Spanish) + " %",
                    Decode(sections[index]),
                    StringComparison.Ordinal);
            }

            if (lines.Count == 1)
            {
                Assert.Equal("376.99 0.00", slices[0].Groups[1].Value);
                sawSingleLine = true;
            }
        }

        Assert.True(sawSingleLine, "The sample must carry a single-line invoice.");
    }

    /// <summary>
    /// An invoice whose twelve months are all zero still prints: the bars are of height 0 on
    /// the baseline and the average line lies on the baseline, with no division by zero.
    /// </summary>
    [Fact]
    public async Task AnAllZeroSeriesPrintsFlatBarsAndAnAverageOnTheBaseline()
    {
        var (code, _, html, _) = await RenderAsync(english: false);
        var invoices = Invoices();
        var index = invoices.FindIndex(invoice => Series(invoice).All(value => value == 0m));
        Assert.True(index >= 0, "The sample must carry an invoice whose consumption is all zero.");
        Assert.Equal(0, code);

        var section = Sections(html)[index];
        var heights = Regex.Matches(section, "<rect class=\"bar\"[^>]* y=\"([^\"]*)\"[^>]* height=\"([^\"]*)\">", RegexOptions.None, RegexTimeout);
        Assert.Equal(12, heights.Count);
        Assert.All(heights, bar =>
        {
            Assert.Equal("110.00", bar.Groups[1].Value);
            Assert.Equal("0.00", bar.Groups[2].Value);
        });
        Assert.Contains("<line class=\"average\" x1=\"0\" y1=\"110.00\" x2=\"370\" y2=\"110.00\">", section, StringComparison.Ordinal);
    }

    /// <summary>The example carries no template code: no script element and no event-handler attribute.</summary>
    [Fact]
    public void TheChartsExampleCarriesNoScript()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestPaths.Example(Id), "html"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                Regex.IsMatch(text, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase, RegexTimeout),
                Path.GetFileName(file) + " carries an event-handler attribute.");
        }
    }

    /// <summary>
    /// The guide's chart snippets are the sample's own lines: every non-blank line of the
    /// code blocks under "Drawing a chart" is, trimmed, a line of the example's row part.
    /// </summary>
    [Fact]
    public void TheGuidesChartSnippetsAreTakenFromTheSample()
    {
        var guide = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "docs", "template-authoring.md")).ReplaceLineEndings("\n");
        var start = guide.IndexOf("\n### Drawing a chart\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "The guide has no \"Drawing a chart\" section.");
        var end = guide.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? guide[start..] : guide[start..end];

        var blocks = Regex.Matches(section, "```text\n(.*?)```", RegexOptions.Singleline, RegexTimeout);
        Assert.Equal(2, blocks.Count);

        var row = File.ReadAllText(Path.Combine(TestPaths.Example(Id), "html", "row.html.liquid"))
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToHashSet(StringComparer.Ordinal);
        foreach (Match block in blocks)
        {
            foreach (var line in block.Groups[1].Value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                Assert.True(row.Contains(line), "The guide's snippet line is not in the sample's row part: " + line);
            }
        }
    }

    private static void AssertInvariantNumber(string name, string value) =>
        Assert.True(
            Regex.IsMatch(value, "^-?[0-9]+(\\.[0-9]+)?$", RegexOptions.None, RegexTimeout),
            name + "=\"" + value + "\" is not invariant number text.");

    private static string Text(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);

    private static async Task<(int Code, string Output, string Html, ReplayPrinter Printer)> RenderAsync(bool english)
    {
        var root = TestPaths.Temp();
        var package = TestPaths.Example(Id);
        if (english)
        {
            package = CopyPackage(root);
            var manifestPath = Path.Combine(package, "template.yaml");
            var manifest = File.ReadAllText(manifestPath);
            var changed = manifest.Replace("  culture: es-AR", "  culture: en-US", StringComparison.Ordinal);
            Assert.NotEqual(manifest, changed);
            File.WriteAllText(manifestPath, changed, Utf8NoBom);
        }

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, (english ? EnglishGolden : Id) + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var htmlPath = Path.Combine(outputDirectory, Id + "-1.0.0.html");
        var html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : string.Empty;
        return (code, writer.ToString(), html, printer);
    }

    private static List<string> ChartSvgs(string html) =>
        [.. Regex.Matches(html, "<svg class=\"(?:bars-chart|donut)\".*?</svg>", RegexOptions.Singleline, RegexTimeout).Select(match => match.Value)];

    private static List<string> Sections(string html) =>
        [.. html.Split(InvoiceOpening).Skip(1)];

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static List<JsonElement> Invoices()
    {
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(Id), "sample.json"))).RootElement;
        return [.. sample.GetProperty("items").EnumerateArray()];
    }

    private static List<decimal> Series(JsonElement invoice) =>
        [.. invoice.GetProperty("consumption").EnumerateArray().Select(month => month.GetProperty("kwh").GetDecimal())];

    private static TemplateAuthoringTool Tool(IPdfPrinter printer) =>
        new(new FixedTimeProvider(FixedInstant), () => printer);

    private static string CopyPackage(string root)
    {
        var source = TestPaths.Example(Id);
        var target = Path.Combine(root, Id);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}
