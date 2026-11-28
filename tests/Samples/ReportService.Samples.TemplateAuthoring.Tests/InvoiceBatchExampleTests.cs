using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Rendering;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The invoice batch examples: one record per invoice with its nested lines, its
/// precomputed barcode geometry and its flags, printed in two layouts the template
/// chooses. Every expectation is read from the package's own <c>sample.json</c>, never
/// from the composed document; the golden replays the live print.
/// </summary>
public sealed class InvoiceBatchExampleTests
{
    private const string OnePerPage = "invoice-batch";

    private const string TwoPerPage = "invoice-batch-two-up";

    private const string InvoiceOpening = "<section class=\"invoice\">";

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Each invoice example renders, and the product's verification accepts the printed page.</summary>
    /// <param name="id">The example package's identifier.</param>
    [Theory]
    [InlineData(OnePerPage)]
    [InlineData(TwoPerPage)]
    public async Task EachInvoiceExampleRendersAndVerifies(string id)
    {
        var (code, output, html, printer) = await RenderAsync(id);

        Assert.Equal(0, code);
        Assert.Contains("rendered " + id + " 1.0.0: engine=liquid", output, StringComparison.Ordinal);
        Assert.Equal(printer.Document, html);
    }

    /// <summary>The layout is the template's: one invoice per page prints six pages, two per page prints three.</summary>
    /// <param name="id">The example package's identifier.</param>
    /// <param name="pages">The pages the layout prints for the six-invoice sample.</param>
    [Theory]
    [InlineData(OnePerPage, 6)]
    [InlineData(TwoPerPage, 3)]
    public async Task TheTemplateDecidesHowManyInvoicesAPageCarries(string id, int pages)
    {
        var (code, output, _, _) = await RenderAsync(id);

        Assert.Equal(0, code);
        Assert.Equal(6, Invoices(id).Count);
        Assert.Contains(" pages=" + pages.ToString(CultureInfo.InvariantCulture) + " ", output, StringComparison.Ordinal);
    }

    /// <summary>Every invoice prints once, in the sample's order, with every one of its lines.</summary>
    [Fact]
    public async Task EveryInvoicePrintsOnceInOrderWithItsLines()
    {
        var (_, _, html, _) = await RenderAsync(OnePerPage);
        var sections = Sections(html);
        var invoices = Invoices(OnePerPage);

        Assert.Equal(invoices.Count, sections.Count);
        for (var index = 0; index < invoices.Count; index++)
        {
            var invoice = invoices[index];
            var number = invoice.GetProperty("invoiceNumber").GetString()!;
            Assert.Contains("Factura " + number, sections[index], StringComparison.Ordinal);
            Assert.Single(sections, section => section.Contains("Factura " + number, StringComparison.Ordinal));
            foreach (var line in invoice.GetProperty("lines").EnumerateArray())
            {
                Assert.Contains(line.GetProperty("description").GetString()!, sections[index], StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A flagged field or graphic prints exactly when its precomputed flag is true.</summary>
    [Fact]
    public async Task FlaggedContentPrintsOnlyWhenItsFlagIsTrue()
    {
        var (_, _, html, _) = await RenderAsync(OnePerPage);
        var sections = Sections(html);
        var invoices = Invoices(OnePerPage);

        Assert.Contains(invoices, invoice => !Flag(invoice, "showBarcode"));
        Assert.Contains(invoices, invoice => Flag(invoice, "paid"));
        Assert.Contains(invoices, invoice => Flag(invoice, "showDueNotice"));
        Assert.Contains(invoices, invoice => !Flag(invoice, "showDueNotice"));

        for (var index = 0; index < invoices.Count; index++)
        {
            var invoice = invoices[index];
            var section = sections[index];
            Assert.Equal(Flag(invoice, "showBarcode"), section.Contains("<svg", StringComparison.Ordinal));
            Assert.Equal(
                Flag(invoice, "showBarcode"),
                section.Contains("<figcaption>" + invoice.GetProperty("barcode").GetProperty("value").GetString(), StringComparison.Ordinal));
            Assert.Equal(Flag(invoice, "showDueNotice"), section.Contains(invoice.GetProperty("dueNotice").GetString()!, StringComparison.Ordinal));
            Assert.Equal(Flag(invoice, "paid"), section.Contains("PAGADA", StringComparison.Ordinal));
        }
    }

    /// <summary>The barcode prints exactly the bars the record carried, at the positions it carried.</summary>
    [Fact]
    public async Task TheBarcodePrintsThePrecomputedBarsAsCarried()
    {
        var (_, _, html, _) = await RenderAsync(OnePerPage);
        var sections = Sections(html);
        var invoices = Invoices(OnePerPage);
        var index = invoices.FindIndex(invoice => Flag(invoice, "showBarcode"));
        Assert.True(index >= 0);

        var bars = invoices[index].GetProperty("barcode").GetProperty("bars").EnumerateArray().ToList();
        var printed = Regex.Matches(sections[index], "<rect x=\"(\\d+)\" y=\"0\" width=\"(\\d+)\"", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.Equal(bars.Count, printed.Count);
        for (var bar = 0; bar < bars.Count; bar++)
        {
            Assert.Equal(bars[bar].GetProperty("x").GetInt32().ToString(CultureInfo.InvariantCulture), printed[bar].Groups[1].Value);
            Assert.Equal(bars[bar].GetProperty("w").GetInt32().ToString(CultureInfo.InvariantCulture), printed[bar].Groups[2].Value);
        }
    }

    /// <summary>The examples carry no template code: no script element and no event-handler attribute.</summary>
    /// <param name="id">The example package's identifier.</param>
    [Theory]
    [InlineData(OnePerPage)]
    [InlineData(TwoPerPage)]
    public void TheExamplesCarryNoTemplateCode(string id)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestPaths.Example(id), "html"), "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
            Assert.False(
                Regex.IsMatch(text, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)),
                Path.GetFileName(file) + " carries an event-handler attribute.");
        }
    }

    /// <summary>
    /// A record value may be written as an image's <c>src</c>, and the sample render checks
    /// it: a value that is not an image data URI - here the barcode's text - is refused
    /// before anything is printed, naming the part, the record and the attribute.
    /// </summary>
    [Fact]
    public async Task AValueThatIsNotAnImageDataUriInAnImageSourceIsRefusedBySampleRender()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(OnePerPage, root);
        var rowPath = Path.Combine(package, "html", "row.html.liquid");
        var row = File.ReadAllText(rowPath);
        var changed = row.Replace(
            "<figcaption>",
            "<img src=\"{{ item.barcode.value }}\" alt=\"\"><figcaption>",
            StringComparison.Ordinal);
        Assert.NotEqual(row, changed);
        File.WriteAllText(rowPath, changed, Utf8NoBom);

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, OnePerPage + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Equal(0, printer.Calls);
        Assert.Contains("html/row.html.liquid", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("attribute=src", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    private static async Task<(int Code, string Output, string Html, ReplayPrinter Printer)> RenderAsync(string id)
    {
        var outputDirectory = Path.Combine(TestPaths.Temp(), "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, id + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(TestPaths.Example(id), outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var htmlPath = Path.Combine(outputDirectory, id + "-1.0.0.html");
        var html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : string.Empty;
        return (code, writer.ToString(), html, printer);
    }

    private static List<string> Sections(string html) =>
        [.. WebUtility.HtmlDecode(html).Split(InvoiceOpening).Skip(1)];

    private static List<JsonElement> Invoices(string id)
    {
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(id), "sample.json"))).RootElement;
        return [.. sample.GetProperty("items").EnumerateArray()];
    }

    private static bool Flag(JsonElement invoice, string name) =>
        invoice.GetProperty("flags").GetProperty(name).GetBoolean();

    private static TemplateAuthoringTool Tool(IPdfPrinter printer) =>
        new(new FixedTimeProvider(FixedInstant), () => printer);

    private static string CopyPackage(string id, string root)
    {
        var source = TestPaths.Example(id);
        var target = Path.Combine(root, id);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }
}
