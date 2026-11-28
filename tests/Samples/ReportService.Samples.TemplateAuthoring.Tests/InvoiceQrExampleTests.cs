using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportService.Rendering;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The QR example: every record is an invoice carrying its own QR as a PNG image data URI
/// the producer computed before the job, and the row part writes it as the quoted source
/// of an image. Every expectation is read from the package's own <c>sample.json</c>, never
/// from the composed document; the PDF golden replays the live print and the HTML golden
/// is the document that live render composed.
/// </summary>
public sealed class InvoiceQrExampleTests
{
    private const string Id = "invoice-qr";

    private const string InvoiceOpening = "<section class=\"invoice\">";

    private const string PngDataUriPrefix = "data:image/png;base64,";

    private const int ImageCeilingBytes = 64 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The example renders six pages without a warning, the product's verification accepts
    /// the printed PDF, and the composed HTML is the HTML golden byte for byte.
    /// </summary>
    [Fact]
    public async Task TheQrExampleRendersVerifiesAndMatchesItsHtmlGolden()
    {
        var (code, output, html, printer) = await RenderAsync(TestPaths.Example(Id));

        Assert.Equal(0, code);
        Assert.Contains("rendered " + Id + " 1.0.0: engine=liquid pages=6 ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", output, StringComparison.Ordinal);
        Assert.Equal(printer.Document, html);
        Assert.Equal(File.ReadAllText(Path.Combine(TestPaths.Goldens, Id + ".html")), html);
    }

    /// <summary>
    /// Every invoice whose flag asks for it prints exactly one image whose source is the QR
    /// its own record carried; an invoice whose flag is false prints none; no two invoices
    /// print the same image.
    /// </summary>
    [Fact]
    public async Task EachInvoicePrintsItsOwnQrImageFromItsRecord()
    {
        var (_, _, html, _) = await RenderAsync(TestPaths.Example(Id));
        var sections = Sections(html);
        var invoices = Invoices();

        Assert.Equal(invoices.Count, sections.Count);
        Assert.Contains(invoices, invoice => !ShowsQr(invoice));
        Assert.Contains(invoices, ShowsQr);

        var printed = new List<string>();
        for (var index = 0; index < invoices.Count; index++)
        {
            var sources = Regex.Matches(sections[index], "<img class=\"qr-code\" src=\"([^\"]*)\"", RegexOptions.None, RegexTimeout)
                .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
                .ToList();
            if (ShowsQr(invoices[index]))
            {
                Assert.Equal(Image(invoices[index]), Assert.Single(sources));
                printed.Add(sources[0]);
            }
            else
            {
                Assert.Empty(sources);
                Assert.DoesNotContain("<img", sections[index], StringComparison.Ordinal);
            }
        }

        Assert.Equal(printed.Count, printed.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Every QR the sample carries is a base64 PNG image data URI well under the bounds an
    /// image data URI is subject to.
    /// </summary>
    [Fact]
    public void EveryQrInTheSampleIsASmallPngDataUri()
    {
        foreach (var invoice in Invoices())
        {
            var image = Image(invoice);
            Assert.StartsWith(PngDataUriPrefix, image, StringComparison.Ordinal);
            Assert.True(Encoding.UTF8.GetByteCount(image) <= ImageCeilingBytes, "A QR image exceeds the sample's ceiling.");
            var bytes = Convert.FromBase64String(image[PngDataUriPrefix.Length..]);
            Assert.Equal(PngSignature, bytes.Take(PngSignature.Length).ToArray());
        }
    }

    /// <summary>
    /// A record whose image is a remote address, or a relative reference, is refused by the
    /// sample render before anything is printed: the tool reports it as an invalid template
    /// whose image source is not an image data URI, naming the part, the record and the
    /// attribute and never the value (a job's output reports the same check with its own
    /// image-source code).
    /// The copy relaxes the schema's pattern for the image so that the render's own check of
    /// the image source, not the schema, is what refuses it.
    /// </summary>
    /// <param name="value">The value written into the second invoice's image.</param>
    [Theory]
    [InlineData("https://example.invalid/qr.png")]
    [InlineData("qr.png")]
    public async Task AnImageThatIsNotADataUriIsRefusedByTheRender(string value)
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(root);

        var schemaPath = Path.Combine(package, "schema.json");
        var schema = File.ReadAllText(schemaPath);
        var relaxed = schema.Replace(
            ", \"pattern\": \"^data:image/png;base64,[A-Za-z0-9+/]+={0,2}$\"",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(schema, relaxed);
        File.WriteAllText(schemaPath, relaxed, Utf8NoBom);

        var samplePath = Path.Combine(package, "sample.json");
        var sample = File.ReadAllText(samplePath);
        var original = Image(Invoices()[1]);
        var changed = sample.Replace(original, value, StringComparison.Ordinal);
        Assert.NotEqual(sample, changed);
        File.WriteAllText(samplePath, changed, Utf8NoBom);

        var (code, output, _, printer) = await RenderAsync(package);

        Assert.Equal(1, code);
        Assert.Equal(0, printer.Calls);
        Assert.StartsWith("TEMPLATE_INVALID: html/row.html.liquid: the source of an image is not an image data URI", output, StringComparison.Ordinal);
        Assert.Contains("(part=row ordinal=2 attribute=src)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(value, output, StringComparison.Ordinal);
    }

    /// <summary>The example carries no template code: no script element and no event-handler attribute.</summary>
    [Fact]
    public void TheExampleCarriesNoTemplateCode()
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

    private static async Task<(int Code, string Output, string Html, ReplayPrinter Printer)> RenderAsync(string package)
    {
        var outputDirectory = Path.Combine(TestPaths.Temp(), "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, Id + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await new TemplateAuthoringTool(new FixedTimeProvider(FixedInstant), () => printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var htmlPath = Path.Combine(outputDirectory, Id + "-1.0.0.html");
        var html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath) : string.Empty;
        return (code, writer.ToString(), html, printer);
    }

    private static List<string> Sections(string html) =>
        [.. html.Split(InvoiceOpening).Skip(1)];

    private static List<JsonElement> Invoices()
    {
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(Id), "sample.json"))).RootElement;
        return [.. sample.GetProperty("items").EnumerateArray()];
    }

    private static bool ShowsQr(JsonElement invoice) =>
        invoice.GetProperty("flags").GetProperty("showQr").GetBoolean();

    private static string Image(JsonElement invoice) =>
        invoice.GetProperty("qr").GetProperty("image").GetString()!;

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
