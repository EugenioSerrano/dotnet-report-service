using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReportService.Pdf.Assembly;
using ReportService.Rendering;
using ReportService.TemplateChecks.PdfConstraints;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Manifest;
using ReportService.Templates;

namespace ReportService.Samples.TemplateAuthoring.Tests;

/// <summary>
/// The authoring tool's hermetic cases: each engine's scaffold is a package the take's
/// own checks accept; each example renders to the printed HTML and a PDF the product's
/// verification accepts (the golden replaying the print); and each refusal is the
/// take's own sentence, naming the file and never a template text or a data value.
/// </summary>
public sealed class AuthoringToolTests
{
    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Scaffolding each engine writes exactly the package files and the take's checks accept it.</summary>
    /// <param name="engine">The engine's manifest name.</param>
    [Theory]
    [InlineData("scriban")]
    [InlineData("handlebars")]
    [InlineData("liquid")]
    public void ScaffoldingEachEngineWritesAPackageTheTakeChecksAccept(string engine)
    {
        var target = Path.Combine(TestPaths.Temp(), "sample-report");
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        var code = new TemplateAuthoringTool().Scaffold(new ScaffoldRequest(engine, "sample-report", target), writer);

        Assert.Equal(0, code);
        Assert.Contains("scaffolded " + engine + " sample-report 1.0.0", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("checks=pass", writer.ToString(), StringComparison.Ordinal);

        var extension = ExtensionOf(engine);
        var expected = new[]
        {
            "html/document-end.html" + extension,
            "html/document-start.html" + extension,
            "html/row.html" + extension,
            "html/styles.css",
            "sample.json",
            "schema.json",
            "template.yaml",
        };
        var actual = Directory
            .EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);

        var manifest = ReadManifest(target);
        Assert.NotNull(manifest.Engine);
        Assert.Equal(engine, TemplateEngineNames.ToManifestName(manifest.Engine!.Value));
        var adapter = TemplateEngineAdapters.All().Single(candidate => string.Equals(candidate.Engine, engine, StringComparison.Ordinal));
        Assert.Equal(adapter.Version, manifest.DeclaredEngineVersion);
        Assert.Equal("html/row.html" + extension, manifest.Outputs!.Html!.Parts!.Row);
        Assert.Equal("html/document-start.html" + extension, manifest.Outputs.Html.Parts.DocumentStart);
        Assert.Equal("html/document-end.html" + extension, manifest.Outputs.Html.Parts.DocumentEnd);
    }

    /// <summary>A target that already holds a file is refused and left untouched.</summary>
    [Fact]
    public void ScaffoldingRefusesANonEmptyTarget()
    {
        var target = Path.Combine(TestPaths.Temp(), "sample-report");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "keep.txt");
        File.WriteAllText(existing, "keep", Utf8NoBom);

        var code = new TemplateAuthoringTool().Scaffold(
            new ScaffoldRequest("scriban", "sample-report", target),
            new StringWriter(CultureInfo.InvariantCulture));

        Assert.Equal(2, code);
        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.Single(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
    }

    /// <summary>An unknown engine and an identifier outside the documented rule are usage errors.</summary>
    [Fact]
    public void ScaffoldingRefusesAnUnknownEngineAndAnInvalidId()
    {
        var root = TestPaths.Temp();
        var tool = new TemplateAuthoringTool();

        var unknownEngine = tool.Scaffold(
            new ScaffoldRequest("razor", "sample-report", Path.Combine(root, "engine")),
            new StringWriter(CultureInfo.InvariantCulture));
        var invalidId = tool.Scaffold(
            new ScaffoldRequest("scriban", "Sample_Report", Path.Combine(root, "id")),
            new StringWriter(CultureInfo.InvariantCulture));

        Assert.Equal(2, unknownEngine);
        Assert.Equal(2, invalidId);
        Assert.False(Directory.Exists(Path.Combine(root, "engine")));
        Assert.False(Directory.Exists(Path.Combine(root, "id")));
    }

    /// <summary>Each example renders to the document the printer received and a verified PDF.</summary>
    /// <param name="id">The example package's identifier.</param>
    [Theory]
    [InlineData("pilot-simple")]
    [InlineData("pilot-handlebars")]
    [InlineData("pilot-liquid")]
    public async Task EachExampleRendersToThePrintedHtmlAndAVerifiedPdf(string id)
    {
        var root = TestPaths.Temp();
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, id + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(TestPaths.Example(id), outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, code);
        Assert.Contains("rendered " + id + " 1.0.0", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning:", writer.ToString(), StringComparison.Ordinal);

        var htmlPath = Path.Combine(outputDirectory, id + "-1.0.0.html");
        var pdfPath = Path.Combine(outputDirectory, id + "-1.0.0.pdf");
        Assert.True(File.Exists(htmlPath));
        Assert.True(File.Exists(pdfPath));
        Assert.True(new FileInfo(pdfPath).Length > 0);

        var written = File.ReadAllText(htmlPath);
        Assert.Equal(printer.Document, written);

        // The engines encode every value; the text a reader sees carries the sample's.
        var printed = WebUtility.HtmlDecode(written);
        var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(id), "sample.json"))).RootElement;
        var header = sample.GetProperty("header");
        Assert.Contains(header.GetProperty("reportTitle").GetString()!, printed, StringComparison.Ordinal);
        Assert.Contains(header.GetProperty("customer").GetProperty("name").GetString()!, printed, StringComparison.Ordinal);
        var items = sample.GetProperty("items");
        Assert.Contains(items[0].GetProperty("description").GetString()!, printed, StringComparison.Ordinal);
        Assert.Contains(items[items.GetArrayLength() - 1].GetProperty("description").GetString()!, printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opened on its own from the output folder, the written preview reaches every
    /// stylesheet it links, and each is the very file the print was served for that
    /// reference; the preview itself stays the document the printer received.
    /// </summary>
    /// <param name="id">The example package's identifier.</param>
    [Theory]
    [InlineData("pilot-simple")]
    [InlineData("pilot-handlebars")]
    [InlineData("pilot-liquid")]
    [InlineData("invoice-batch")]
    [InlineData("invoice-batch-two-up")]
    public async Task ThePreviewReachesTheStylesheetsThePrintWasServed(string id)
    {
        var root = TestPaths.Temp();
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, id + ".pdf"));

        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(TestPaths.Example(id), outputDirectory), new StringWriter(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, code);
        var written = File.ReadAllText(Path.Combine(outputDirectory, id + "-1.0.0.html"));
        Assert.Equal(printer.Document, written);

        var links = StylesheetLinks(written);
        Assert.NotEmpty(links);
        foreach (var href in links)
        {
            AssertServedFileIsBeside(TestPaths.Example(id), outputDirectory, new Uri(new Uri(PackageRoutePolicy.DocumentUrl), href));
        }
    }

    /// <summary>
    /// A file the linked stylesheet names - an image here - is reached from the output
    /// folder as well, as the very file the print would be served for it.
    /// </summary>
    [Fact]
    public async Task AFileTheStylesheetNamesIsBesideThePreviewToo()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-liquid", root);
        File.WriteAllBytes(Path.Combine(package, "html", "mark.png"), MarkPng);
        File.AppendAllText(Path.Combine(package, "html", "styles.css"), "\n.mark { background-image: url(\"mark.png\"); }\n", Utf8NoBom);
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-liquid.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(code == 0, writer.ToString());
        Assert.Equal(printer.Document, File.ReadAllText(Path.Combine(outputDirectory, "pilot-liquid-1.0.0.html")));
        var stylesheet = new Uri(new Uri(PackageRoutePolicy.DocumentUrl), "html/styles.css");
        AssertServedFileIsBeside(package, outputDirectory, stylesheet);
        AssertServedFileIsBeside(package, outputDirectory, new Uri(stylesheet, "mark.png"));
    }

    /// <summary>The two new examples are the same report in another engine as the simple one.</summary>
    [Fact]
    public void TheExamplesMirrorTheSimpleReportContract()
    {
        var simple = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example("pilot-simple"), "sample.json"))).RootElement;
        var simpleSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example("pilot-simple"), "schema.json"))).RootElement;
        var simpleHeader = simple.GetProperty("header");
        var simpleItems = simple.GetProperty("items");

        foreach (var id in new[] { "pilot-handlebars", "pilot-liquid" })
        {
            var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(id), "sample.json"))).RootElement;
            var header = sample.GetProperty("header");
            foreach (var member in simpleHeader.EnumerateObject())
            {
                if (string.Equals(member.Name, "datasetId", StringComparison.Ordinal))
                {
                    Assert.Equal(id, header.GetProperty("datasetId").GetString());
                    continue;
                }

                Assert.Equal(member.Value.GetRawText(), header.GetProperty(member.Name).GetRawText());
            }

            var items = sample.GetProperty("items");
            Assert.Equal(simpleItems.GetArrayLength(), items.GetArrayLength());
            for (var index = 0; index < items.GetArrayLength(); index++)
            {
                Assert.Equal(simpleItems[index].GetRawText(), items[index].GetRawText());
            }

            var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Example(id), "schema.json"))).RootElement;
            var simpleHeaderSchema = simpleSchema.GetProperty("properties").GetProperty("header");
            var headerSchema = schema.GetProperty("properties").GetProperty("header");
            Assert.Equal(Members(simpleHeaderSchema), Members(headerSchema));
            Assert.Equal(Required(simpleHeaderSchema), Required(headerSchema));
            Assert.Equal(id, headerSchema.GetProperty("properties").GetProperty("datasetId").GetProperty("const").GetString());

            var simpleRecordSchema = simpleSchema.GetProperty("$defs").GetProperty("item");
            var recordSchema = schema.GetProperty("$defs").GetProperty("item");
            Assert.Equal(Members(simpleRecordSchema), Members(recordSchema));
            Assert.Equal(Required(simpleRecordSchema), Required(recordSchema));
        }
    }

    /// <summary>A render writes the preview, the PDF and the stylesheet the preview links under the output directory, and changes nothing else.</summary>
    [Fact]
    public async Task TheRenderWritesOnlyUnderTheOutputDirectory()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-liquid", root);
        var before = PackageDigest.Of(package);
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-liquid.pdf"));

        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), new StringWriter(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, code);
        Assert.Equal(before, PackageDigest.Of(package));
        Assert.Equal(
            ["pilot-liquid-1.0.0.html", "pilot-liquid-1.0.0.pdf", "styles.css"],
            Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path))
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["out", "out/html", "pilot-liquid", "pilot-liquid/html"],
            Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>An anchor the row no longer prints is refused before anything is printed.</summary>
    [Fact]
    public async Task AnAnchorTheRowNoLongerPrintsIsRefusedBeforeAnythingIsPrinted()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-handlebars", root);
        var rowPath = Path.Combine(package, "html", "row.html.hbs");
        var row = File.ReadAllText(rowPath);
        var changed = row.Replace("<td class=\"num\">{{item.sequence}}</td>", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(row, changed);
        File.WriteAllText(rowPath, changed, Utf8NoBom);

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-handlebars.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Equal(0, printer.Calls);
        Assert.Contains("template.yaml", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("$.sequence", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("anchor", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>An unescaped data output is refused by the take's lint, in each engine's own form.</summary>
    /// <param name="id">The example package the case edits.</param>
    /// <param name="rowFile">The row part's package-relative path.</param>
    /// <param name="escaped">The escaped output, as the package writes it.</param>
    /// <param name="unescaped">The form the case writes instead.</param>
    [Theory]
    [InlineData("pilot-simple", "html/row.html.sbn", "{{ item.description | html.escape }}", "{{ item.description }}")]
    [InlineData("pilot-handlebars", "html/row.html.hbs", "{{item.description}}", "{{{item.description}}}")]
    [InlineData("pilot-liquid", "html/row.html.liquid", "{{ item.description }}", "{{ item.description | raw }}")]
    public async Task AnUnescapedDataOutputIsRefusedByTheTakeLint(
        string id,
        string rowFile,
        string escaped,
        string unescaped)
    {
        var root = TestPaths.Temp();
        var package = CopyPackage(id, root);
        var rowPath = Path.Combine(package, rowFile.Replace('/', Path.DirectorySeparatorChar));
        var row = File.ReadAllText(rowPath);
        var changed = row.Replace(escaped, unescaped, StringComparison.Ordinal);
        Assert.NotEqual(row, changed);
        File.WriteAllText(rowPath, changed, Utf8NoBom);

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, id + ".pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Equal(0, printer.Calls);
        Assert.Contains(rowFile, writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A printed page that does not carry the composition is refused, and no value is echoed.</summary>
    [Fact]
    public async Task APrintedPageThatDoesNotCarryTheCompositionIsRefused()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-handlebars", root);
        var samplePath = Path.Combine(package, "sample.json");
        var sample = JsonNode.Parse(File.ReadAllText(samplePath))!;
        sample["items"]!.AsArray()[0]!["description"] = "Changed description";
        File.WriteAllText(samplePath, sample.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-handlebars.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Contains("html/row.html.hbs", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("does not carry", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Changed description", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A render that cannot run is retryable: exit 3 and never a refusal of the package.</summary>
    [Fact]
    public async Task AnUnavailableRenderIsRetryableNotARefusal()
    {
        var root = TestPaths.Temp();
        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf"), unavailable: true);
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(TestPaths.Example("pilot-simple"), outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(3, code);
        Assert.Contains("render-unavailable", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A package folder without a manifest is a usage error.</summary>
    [Fact]
    public async Task APackageWithoutAManifestIsAUsageError()
    {
        var root = TestPaths.Temp();
        var package = Path.Combine(root, "empty-package");
        Directory.CreateDirectory(package);

        var code = await Tool(new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf")))
            .RenderAsync(new RenderRequest(package, Path.Combine(root, "out")), new StringWriter(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(2, code);
    }

    /// <summary>A manifest this build cannot type is refused with the take's own sentence.</summary>
    [Fact]
    public async Task AManifestThisBuildCannotTypeIsRefusedWithTheTakeSentence()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-simple", root);
        var manifestPath = Path.Combine(package, "template.yaml");
        var text = File.ReadAllText(manifestPath);
        var changed = text.Replace("shared: false", "shared: maybe", StringComparison.Ordinal);
        Assert.NotEqual(text, changed);
        File.WriteAllText(manifestPath, changed, Utf8NoBom);

        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf")))
            .RenderAsync(new RenderRequest(package, Path.Combine(root, "out")), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Contains(
            "template.yaml: the manifest declares a member with a type this service does not read.",
            writer.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>A package without its sample is refused by the take's static sample check.</summary>
    [Fact]
    public async Task APackageWithoutASampleIsRefused()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-simple", root);
        File.Delete(Path.Combine(package, "sample.json"));

        var outputDirectory = Path.Combine(root, "out");
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf")))
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Contains("sample.json", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>A sample larger than one block is refused by the take's own over-bound sentence.</summary>
    [Fact]
    public async Task AnOverBoundSampleIsRefused()
    {
        var root = TestPaths.Temp();
        var package = CopyPackage("pilot-simple", root);
        var samplePath = Path.Combine(package, "sample.json");
        var sample = JsonNode.Parse(File.ReadAllText(samplePath))!;
        var items = new JsonArray();
        for (var sequence = 1; sequence <= 2001; sequence++)
        {
            items.Add(new JsonObject
            {
                ["sequence"] = sequence,
                ["description"] = "Item " + sequence.ToString(CultureInfo.InvariantCulture),
                ["quantity"] = 1,
                ["unitPrice"] = 100.00m,
                ["amount"] = 100.00m,
            });
        }

        sample["items"] = items;
        sample["header"]!["totals"]!["recordCount"] = 2001;
        sample["header"]!["totals"]!["amount"] = 200100.00m;
        File.WriteAllText(samplePath, sample.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var outputDirectory = Path.Combine(root, "out");
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(package, outputDirectory), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Equal(0, printer.Calls);
        Assert.Contains("one block holds at most", writer.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    /// <summary>An output directory that already holds the written files has them replaced and no other file touched.</summary>
    [Fact]
    public async Task AnExistingOutputDirectoryIsOverwrittenWithoutTouchingOtherFiles()
    {
        var root = TestPaths.Temp();
        var outputDirectory = Path.Combine(root, "out");
        Directory.CreateDirectory(outputDirectory);
        var notes = Path.Combine(outputDirectory, "notes.txt");
        File.WriteAllText(notes, "keep", Utf8NoBom);
        File.WriteAllText(Path.Combine(outputDirectory, "pilot-simple-1.0.0.html"), "old", Utf8NoBom);
        File.WriteAllText(Path.Combine(outputDirectory, "pilot-simple-1.0.0.pdf"), "old", Utf8NoBom);

        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-simple.pdf"));
        var code = await Tool(printer)
            .RenderAsync(new RenderRequest(TestPaths.Example("pilot-simple"), outputDirectory), new StringWriter(CultureInfo.InvariantCulture), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, code);
        Assert.Equal("keep", File.ReadAllText(notes));
        Assert.Equal(printer.Document, File.ReadAllText(Path.Combine(outputDirectory, "pilot-simple-1.0.0.html")));
        Assert.True(new FileInfo(Path.Combine(outputDirectory, "pilot-simple-1.0.0.pdf")).Length > 3);
        Assert.Equal(
            ["notes.txt", "pilot-simple-1.0.0.html", "pilot-simple-1.0.0.pdf", "styles.css"],
            Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>A stated capacity of 100 000 pages refuses a numbering area that fits only 10 000.</summary>
    [Fact]
    public async Task AStatedPageCapacityIsTheOneTheNumberingCheckMeasuresAt()
    {
        var root = TestPaths.Temp();
        var package = WithNumbering(CopyPackage("pilot-liquid", root), MidpointMm("Page 10000 of 10000", "Page 100000 of 100000"));
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-liquid.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        var code = await new TemplateAuthoringTool(new FixedTimeProvider(FixedInstant), () => printer, new NumberingCapacity(100_000))
            .RenderAsync(new RenderRequest(package, Path.Combine(root, "out")), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, code);
        Assert.Contains("outputs.pdf.numbering.format does not fit", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("at the deployment's document capacity of 100000 pages.", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Without a stated capacity the tool measures at the shipped example profile's.</summary>
    [Fact]
    public async Task WithoutAStatedCapacityTheNumberingCheckMeasuresAtTheExampleProfile()
    {
        var root = TestPaths.Temp();
        var package = WithNumbering(CopyPackage("pilot-liquid", root), MidpointMm("Page 10000 of 10000", "Page 100000 of 100000"));
        var printer = new ReplayPrinter(Path.Combine(TestPaths.Goldens, "pilot-liquid.pdf"));
        var writer = new StringWriter(CultureInfo.InvariantCulture);

        await Tool(printer)
            .RenderAsync(new RenderRequest(package, Path.Combine(root, "out")), writer, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.DoesNotContain("outputs.pdf.numbering", writer.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("100000", 100_000)]
    [InlineData("0100", 100)]
    [InlineData("1", 1)]
    public void APageCapacityOfDigitsIsRead(string value, int expected) =>
        Assert.Equal(expected, PageCapacityOption.Parse(value)!.MaximumPages);

    [Fact]
    public void AnAbsentPageCapacityIsNoCapacity() => Assert.Null(PageCapacityOption.Parse(null));

    [Theory]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("-1")]
    [InlineData("+100")]
    [InlineData(" 100")]
    [InlineData("100 ")]
    [InlineData("1,000")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public void AnythingButAPositivePageCountIsRefusedNamingTheOption(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => PageCapacityOption.Parse(value));

        Assert.Contains(PageCapacityOption.Name, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.ParamName);
    }

    private static string WithNumbering(string package, double widthMm)
    {
        var fonts = Path.Combine(package, "fonts");
        Directory.CreateDirectory(fonts);
        File.Copy(PackagedFont, Path.Combine(fonts, "LiberationSans-Regular.ttf"), overwrite: true);
        var manifestPath = Path.Combine(package, "template.yaml");
        var manifest = File.ReadAllText(manifestPath);
        var newline = manifest.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var numbered = manifest.Replace(
            "    pageNumbering: global" + newline,
            "    pageNumbering: global" + newline
            + "    numbering:" + newline
            + "      area: { leftMm: 120, topMm: 268, widthMm: " + widthMm.ToString("0.###", CultureInfo.InvariantCulture) + ", heightMm: 8 }" + newline
            + "      font: { family: \"Liberation Sans\", file: fonts/LiberationSans-Regular.ttf, sizePt: 9 }" + newline
            + "      format: \"Page {X} of {Y}\"" + newline
            + "      culture: en-US" + newline,
            StringComparison.Ordinal);
        Assert.NotEqual(manifest, numbered);
        File.WriteAllText(manifestPath, numbered, Utf8NoBom);
        return package;
    }

    private static string PackagedFont =>
        Path.Combine(TestPaths.RepositoryRoot, "tests", "Support", "ReportService.Testing", "Fonts", "LiberationSans-Regular.ttf");

    /// <summary>The area width, in millimetres, halfway between two stamps' measured widths.</summary>
    private static double MidpointMm(string narrower, string wider)
    {
        var font = new NumberingFont("Liberation Sans", "fonts/LiberationSans-Regular.ttf", 9, File.ReadAllBytes(PackagedFont));
        var probe = new PageNumbering(new NumberingArea(120, 268, 80, 8), font, "Page {X} of {Y}", "en-US");
        var low = NumberingFit.Measure(probe, narrower).WidthPt;
        var high = NumberingFit.Measure(probe, wider).WidthPt;
        Assert.True(low < high);
        return Math.Round((low + high) / 2 / NumberingArea.PointsPerMillimetre, 3);
    }

    private static TemplateAuthoringTool Tool(IPdfPrinter printer) =>
        new(new FixedTimeProvider(FixedInstant), () => printer);

    private static TemplateManifest ReadManifest(string packageDirectory)
    {
        using var stream = File.OpenRead(Path.Combine(packageDirectory, "template.yaml"));
        var read = new TemplateManifestReader().Read(stream);
        Assert.True(read.Succeeded);
        Assert.NotNull(read.Manifest);
        return read.Manifest!;
    }

    private static string ExtensionOf(string engine) => engine switch
    {
        "scriban" => ".sbn",
        "handlebars" => ".hbs",
        _ => ".liquid",
    };

    private static string[] Members(JsonElement schema) =>
        schema.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Required(JsonElement schema) =>
        schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>A 1x1 PNG.</summary>
    private static readonly byte[] MarkPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static string[] StylesheetLinks(string html) =>
        System.Text.RegularExpressions.Regex.Matches(
                html,
                "<link\\b[^>]*\\brel=\"stylesheet\"[^>]*\\bhref=\"([^\"]+)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .ToArray();

    /// <summary>
    /// The print's route policy serves <paramref name="url"/> from the package; the file a
    /// browser reaches for it from the written preview exists and carries the same bytes.
    /// </summary>
    private static void AssertServedFileIsBeside(string package, string outputDirectory, Uri url)
    {
        var decision = new PackageRoutePolicy(new FolderTemplatePackageFiles(package)).Decide(url.AbsoluteUri);
        Assert.Equal(RouteKind.Served, decision.Kind);
        var relative = url.AbsolutePath.TrimStart('/');
        var beside = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(beside), relative + " is not under the output folder.");
        Assert.Equal(File.ReadAllBytes(Path.Combine(package, decision.PackagePath!)), File.ReadAllBytes(beside));
    }

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
