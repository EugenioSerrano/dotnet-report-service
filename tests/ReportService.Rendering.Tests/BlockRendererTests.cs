using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Rendering.Tests;

/// <summary>
/// What the renderer refuses before it composes anything, and what it does with a
/// document it printed: the whole path runs here with a printer that writes the
/// document it was given, so the compose, the checks, the print and the verification
/// are held together without a browser.
/// </summary>
public sealed class BlockRendererTests : IDisposable
{
    /// <summary>The table document the rendering suite keeps as its reader regression case.</summary>
    private const string PrintedDocument = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    private const string A4Portrait = """{"paper":"A4","orientation":"portrait","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}""";

    private const string A4Landscape = """{"paper":"A4","orientation":"landscape","marginMm":{"top":20,"right":15,"bottom":20,"left":15}}""";

    private const string LetterPortrait = """{"paper":"Letter","orientation":"portrait","marginMm":{"top":25.4,"right":19.05,"bottom":25.4,"left":19.05}}""";

    private readonly string work = Path.Combine(Path.GetTempPath(), "block-renderer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string DocumentPath => Path.Combine(AppContext.BaseDirectory, "ReaderCases", PrintedDocument);

    [Fact]
    public async Task APackageWithPageTotalsIsRefusedBeforeAnythingIsPrinted()
    {
        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            Request(Manifest(pageTotals: true, anchors: VerificationAnchors.None), "<p>nothing</p>"),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.PageAccounting, outcome.Verification.Reason);
        Assert.Null(outcome.PdfPath);
        Assert.False(Directory.Exists(work) && Directory.EnumerateFiles(work, "*.pdf").Any());
    }

    [Fact]
    public async Task APackageThatDeclaresNoFieldIsRefusedBeforeAnythingIsPrinted()
    {
        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            Request(Manifest(pageTotals: false, anchors: null), "<p>nothing</p>"),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.AnchorsUndeclared, outcome.Verification.Reason);
        Assert.Null(outcome.PdfPath);
    }

    [Fact]
    public async Task ADocumentThatPrintsAndPassesEveryCheckIsReturnedWithItsHash()
    {
        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            Request(Manifest(pageTotals: false, anchors: VerificationAnchors.None), ComposedTextOfTheDocument()),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Passed, outcome.Verification.Reason);
        Assert.NotNull(outcome.PdfPath);
        Assert.Equal(16, outcome.Verification.Pages);
        Assert.True(outcome.SizeBytes > 0);
        Assert.Equal(64, outcome.Sha256!.Length);
    }

    [Fact]
    public async Task ADocumentThatDoesNotPrintTheComposedTextLeavesNoFileBehind()
    {
        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            Request(Manifest(pageTotals: false, anchors: VerificationAnchors.None), "<p>a text this document does not print at all</p>"),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.NotNull(outcome.Verification.Reason);
        Assert.Null(outcome.PdfPath);
        Assert.False(Directory.Exists(work) && Directory.EnumerateFiles(work, "*.pdf").Any());
    }

    [Fact]
    public async Task ABlockWhoseDeclaredFieldIsNotPrintedIsRefusedBeforeAnythingIsPrinted()
    {
        var manifest = Manifest(pageTotals: false, anchors: new VerificationAnchors([], ["$.description"]));
        var request = Request(manifest, "<p>nothing</p>", [new BlockRecord(1, new Dictionary<string, object?> { ["description"] = "unprinted" })]);
        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            request with { Index = new IndexFacts(0, 1, 1), Partition = new PartitionFacts(1, 1) },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.AnchorMismatch, outcome.Verification.Reason);
        Assert.Equal(1, outcome.Verification.RecordOrdinal);
        Assert.Null(outcome.PdfPath);
        Assert.False(Directory.Exists(work) && Directory.EnumerateFiles(work, "*.pdf").Any());
    }

    [Fact]
    public async Task AnUnusablePageSettingIsRefusedBeforeAnythingIsComposedOrPrinted()
    {
        var manifest = Manifest(
            pageTotals: false,
            anchors: VerificationAnchors.None,
            pdfOutput: """{"orientation":"portrait","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}""");
        var printer = new RecordingPrinter();

        var outcome = await Renderer(printer).RenderAsync(
            Request(manifest, "<p>nothing</p>"),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.PageSettingsInvalid, outcome.Verification.Reason);
        Assert.Equal("outputs.pdf.paper", outcome.Verification.Field);
        Assert.Equal(0, outcome.ComposeMs);
        Assert.False(printer.Called);
        Assert.Null(outcome.PdfPath);
        Assert.False(Directory.Exists(Path.Combine(work, "printed")));
    }

    [Fact]
    public async Task AJobWithoutACultureIsRefusedBeforeAnythingIsComposed()
    {
        var printer = new RecordingPrinter();
        var request = Request(Manifest(pageTotals: false, anchors: VerificationAnchors.None), "<p>nothing</p>");

        var refusal = await Assert.ThrowsAsync<PresentationUnavailable>(() => Renderer(printer).RenderAsync(
            request with { Composition = request.Composition with { Render = request.Composition.Render with { Culture = null } } },
            TestContext.Current.CancellationToken));

        Assert.Equal(PresentationUnavailable.Fields.Culture, refusal.Field);
        Assert.DoesNotContain("nothing", refusal.Message, StringComparison.Ordinal);
        Assert.False(printer.Called);
        Assert.False(Directory.Exists(Path.Combine(work, "printed")));
    }

    [Fact]
    public async Task ThePageRuleOfTheResolvedPageIsGivenToThePrinter()
    {
        var printer = new RecordingPrinter();

        await Renderer(printer).RenderAsync(
            Request(Manifest(pageTotals: false, anchors: VerificationAnchors.None, pdfOutput: A4Landscape), ComposedTextOfTheDocument()),
            TestContext.Current.CancellationToken);

        Assert.True(printer.Called);
        Assert.Equal(
            "@page { size: 297mm 210mm !important; margin: 20mm 15mm 20mm 15mm !important; }",
            printer.Rule!.Css);
        Assert.EndsWith("<style>" + printer.Rule.Css + "</style>", printer.Document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHtmlControlActsAfterThePageRuleWasAppended()
    {
        const string control = "<link rel=\"stylesheet\" href=\"https://report.invalid/html/controls/wrong-size.css\">";
        var printer = new RecordingPrinter();

        await Renderer(printer).RenderAsync(
            Request(Manifest(pageTotals: false, anchors: VerificationAnchors.None), ComposedTextOfTheDocument()),
            blockMutation: null,
            htmlMutation: new AppendingControl(control),
            TestContext.Current.CancellationToken);

        Assert.True(printer.Called);
        Assert.EndsWith(control, printer.Document, StringComparison.Ordinal);
        Assert.True(
            printer.Document!.IndexOf("<style>" + printer.Rule!.Css, StringComparison.Ordinal) < printer.Document.IndexOf(control, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePrintedPageIsCheckedAgainstTheResolvedPageNotTheComposedOne()
    {
        var manifest = Manifest(pageTotals: false, anchors: VerificationAnchors.None, pdfOutput: LetterPortrait);

        var outcome = await Renderer(new PrintingPrinter()).RenderAsync(
            Request(manifest, ComposedTextOfTheDocument()),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Passed);
        Assert.Equal(VerificationReasons.PageSize, outcome.Verification.Reason);
        Assert.Equal(1, outcome.Verification.Page);
        Assert.Equal("Letter", outcome.Verification.Paper);
        Assert.Equal("portrait", outcome.Verification.Orientation);
        Assert.Null(outcome.PdfPath);
    }

    /// <summary>
    /// The text the kept document prints, in reading order, as the composition that
    /// produced it: the words of every page, one paragraph of them per page.
    /// </summary>
    private static string ComposedTextOfTheDocument()
    {
        var reading = PdfWordReader.Read(DocumentPath, PlacementConvention.FromMillimetres(new PageMarginsMm(15, 12, 18, 12)));
        var builder = new StringBuilder();
        foreach (var page in reading.Pages)
        {
            builder.Append("<p>")
                .Append(EscapeScriban(WebUtility.HtmlEncode(string.Concat(
                    page.Words.Where(word => word.Band == ReadingBand.Content).Select(word => word.Text)))))
                .Append("</p>");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The text with the template engine's own delimiters written so the engine prints
    /// them: the document's own words are data, and a data value that looks like a
    /// template instruction must not become one.
    /// </summary>
    private static string EscapeScriban(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var pair = index + 1 < text.Length ? text.Substring(index, 2) : null;
            switch (pair)
            {
                case "{{":
                case "{%":
                    builder.Append("{{ \"").Append(pair).Append("\" }}");
                    index++;
                    continue;
                case "}}":
                case "%}":
                    builder.Append("{{ \"").Append(pair).Append("\" }}");
                    index++;
                    continue;
                default:
                    builder.Append(text[index]);
                    continue;
            }
        }

        return builder.ToString();
    }

    private static BlockRenderer Renderer(IPdfPrinter printer) =>
        new(new BlockComposer(new ScribanTemplateAdapter(), TimeProvider.System), printer, new BlockVerifier());

    /// <summary>A request whose package prints <paramref name="documentStart"/> as its whole document.</summary>
    private BlockRenderRequest Request(RenderManifest manifest, string documentStart, IReadOnlyList<BlockRecord>? records = null)
    {
        var package = Path.Combine(work, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "start.sbn"), documentStart, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(package, "row.sbn"), "<p>something</p>", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var files = new FolderTemplatePackageFiles(package);
        var prepared = new ScribanTemplateAdapter().Prepare(manifest, files);
        var composition = new CompositionRequest(
            prepared,
            new CompositionBlock(0, records?.Count ?? 0, 1, IsFirst: true, IsLast: true),
            new Dictionary<string, object?>(),
            [.. (records ?? []).Select(record => new ComposedRecord(record.Ordinal, record.Values))],
            new CompositionRender("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.UnixEpoch),
            GroupKey: null);

        return new BlockRenderRequest(
            manifest,
            composition,
            files,
            new PartitionFacts(1, 0),
            new IndexFacts(0, 0, 1),
            new BlockFacts(0, 1, records ?? [], new Dictionary<string, object?>()),
            0,
            IsFirstBlock: true,
            Path.Combine(work, "printed"));
    }

    private static RenderManifest Manifest(bool pageTotals, VerificationAnchors? anchors, string pdfOutput = A4Portrait) =>
        new(
            "scriban",
            "7.5.1",
            new Dictionary<TemplatePart, string> { [TemplatePart.DocumentStart] = "start.sbn", [TemplatePart.Row] = "row.sbn" },
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), pageTotals, anchors),
            new LocaleDefaults(null, null),
            GroupKey: null,
            PdfOutput: Pdf(pdfOutput));

    private static JsonElement Pdf(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A printer that writes the kept document, so the verification runs on a real one.</summary>
    private sealed class PrintingPrinter : IPdfPrinter
    {
        public Task<PrintOutcome> PrintAsync(string document, PageRule pageRule, ITemplatePackageFiles files, string pdfPath, CancellationToken cancellationToken = default)
        {
            File.Copy(DocumentPath, pdfPath, overwrite: true);
            return Task.FromResult(new PrintOutcome(pdfPath, 0, 0, TimeSpan.Zero));
        }
    }

    /// <summary>A printer that records the document and the rule it was given, and writes the kept document.</summary>
    private sealed class RecordingPrinter : IPdfPrinter
    {
        public bool Called { get; private set; }

        public string? Document { get; private set; }

        public PageRule? Rule { get; private set; }

        public Task<PrintOutcome> PrintAsync(string document, PageRule pageRule, ITemplatePackageFiles files, string pdfPath, CancellationToken cancellationToken = default)
        {
            Called = true;
            Document = document;
            Rule = pageRule;
            File.Copy(DocumentPath, pdfPath, overwrite: true);
            return Task.FromResult(new PrintOutcome(pdfPath, 0, 0, TimeSpan.Zero));
        }
    }

    /// <summary>The test-only control that acts on the served document after the page rule.</summary>
    private sealed class AppendingControl(string html) : IComposedHtmlMutation
    {
        public string Name => "TestAppendedStyle";

        public string Apply(string document) => document + html;
    }
}
