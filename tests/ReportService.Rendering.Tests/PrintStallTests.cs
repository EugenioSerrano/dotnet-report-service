using System.Net;
using System.Text;
using System.Text.Json;
using ReportService.Pdf.Verification;
using ReportService.Templates;

namespace ReportService.Rendering.Tests;

/// <summary>
/// The print call site of the test-only stall: the renderer awaits it inside the print step
/// and before the printer is asked for a document, so the activity neither writes nor
/// returns - exactly the state the worker's watchdog exists to stop. With no stall installed
/// the print is the delivered one.
/// </summary>
public sealed class PrintStallTests : IDisposable
{
    /// <summary>The table document the rendering suite keeps as its reader regression case.</summary>
    private const string PrintedDocument = "s2-table-s2-T300u-split-t1.00-f0.90-on-r1.pdf";

    private const string A4Portrait = """{"paper":"A4","orientation":"portrait","marginMm":{"top":15,"right":12,"bottom":18,"left":12}}""";

    private readonly string work = Path.Combine(Path.GetTempPath(), "print-stall-" + Guid.NewGuid().ToString("N"));

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
    public async Task ThePrinterIsNotAskedForADocumentUntilTheStallReturns()
    {
        var stall = new GateStall();
        var composer = new BlockComposer(TimeProvider.System) { Stall = stall };
        var printer = new RecordingPrinter();
        var renderer = new BlockRenderer(composer, printer, new BlockVerifier(), TemplateEngineAdapters.Registry());

        var rendering = renderer.RenderAsync(
            Request(Manifest(), ComposedTextOfTheDocument()),
            TestContext.Current.CancellationToken);

        await stall.Entered;
        Assert.Equal(RenderPhase.Print, stall.Phase);
        Assert.False(printer.Called);
        Assert.False(Directory.Exists(Path.Combine(work, "printed")) && Directory.EnumerateFiles(Path.Combine(work, "printed"), "*.pdf").Any());

        stall.Release();
        var outcome = await rendering;

        Assert.True(printer.Called);
        Assert.True(outcome.Passed, outcome.Verification.Reason);
        Assert.Equal(16, outcome.Verification.Pages);
    }

    [Fact]
    public async Task WithNoStallInstalledThePrintIsUnchanged()
    {
        var composer = new BlockComposer(TimeProvider.System);
        var printer = new RecordingPrinter();

        Assert.Null(composer.Stall);

        var outcome = await new BlockRenderer(composer, printer, new BlockVerifier(), TemplateEngineAdapters.Registry())
            .RenderAsync(Request(Manifest(), ComposedTextOfTheDocument()), TestContext.Current.CancellationToken);

        Assert.True(printer.Called);
        Assert.True(outcome.Passed, outcome.Verification.Reason);
        Assert.Equal(1, printer.Calls);
    }

    /// <summary>
    /// The text the kept document prints, in reading order, as the composition that produced
    /// it: the words of every page, one paragraph of them per page.
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

    private BlockRenderRequest Request(RenderManifest manifest, string documentStart)
    {
        var package = Path.Combine(work, "package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "start.sbn"), documentStart, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(Path.Combine(package, "row.sbn"), "<p>something</p>", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var files = new FolderTemplatePackageFiles(package);
        var prepared = new ScribanTemplateAdapter().Prepare(manifest, files);
        var composition = new CompositionRequest(
            prepared,
            new CompositionBlock(0, 0, 1, IsFirst: true, IsLast: true),
            new Dictionary<string, object?>(),
            [],
            new CompositionRender("es-AR", "America/Argentina/Buenos_Aires", DateTimeOffset.UnixEpoch),
            GroupKey: null);

        return new BlockRenderRequest(
            manifest,
            composition,
            files,
            new PartitionFacts(1, 0),
            new IndexFacts(0, 0, 1),
            new BlockFacts(0, 1, [], new Dictionary<string, object?>()),
            0,
            IsFirstBlock: true,
            Path.Combine(work, "printed"));
    }

    private static RenderManifest Manifest() =>
        new(
            "scriban",
            "7.5.1",
            new Dictionary<TemplatePart, string> { [TemplatePart.DocumentStart] = "start.sbn", [TemplatePart.Row] = "row.sbn" },
            [],
            new PdfSettings("A4", "portrait", new PageMarginsMm(15, 12, 18, 12), PageTotalsEnabled: false, VerificationAnchors.None),
            new LocaleDefaults(null, null),
            GroupKey: null,
            PdfOutput: Pdf(A4Portrait));

    private static JsonElement Pdf(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>A stall that holds the print step until the case releases it, and never observes the token.</summary>
    private sealed class GateStall : IRenderStall
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public RenderPhase Phase { get; private set; }

        public async Task StallAsync(RenderPhase phase, CancellationToken cancellationToken)
        {
            if (phase != RenderPhase.Print)
            {
                return;
            }

            Phase = phase;
            _entered.TrySetResult();
            await _released.Task.ConfigureAwait(false);
        }

        public void Release() => _released.TrySetResult();
    }

    /// <summary>A printer that records what it was given and writes the kept document.</summary>
    private sealed class RecordingPrinter : IPdfPrinter
    {
        public bool Called => Calls > 0;

        public int Calls { get; private set; }

        public Task<PrintOutcome> PrintAsync(string document, PageRule pageRule, ITemplatePackageFiles files, string pdfPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            Directory.CreateDirectory(Path.GetDirectoryName(pdfPath)!);
            File.Copy(DocumentPath, pdfPath, overwrite: true);
            return Task.FromResult(new PrintOutcome(pdfPath, 0, 0, TimeSpan.Zero));
        }
    }
}
