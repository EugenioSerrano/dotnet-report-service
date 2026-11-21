using System.Globalization;
using System.Text.Json.Nodes;
using System.Text;
using Microsoft.Extensions.Options;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.Templates;
using ReportService.Testing;
using ReportService.Workers.Pdf.Templates;

namespace ReportService.Workers.Pdf.Tests.Templates;

/// <summary>
/// The sample renderer's boundary, over the real composer, verifier and text
/// extraction and a scripted engine and printer: each class it can meet is classified
/// where the exception is known - the engine's error, its limit, the caller-side
/// guard, a row that prints no text, an unreadable page, a deadline, a crash - and the
/// package-relative part is named. A deadline and a crash are unavailable and never a
/// refusal; a class the renderer cannot name is rethrown. The rendered-and-passed path
/// prints a real page and is the rendering level's case.
/// </summary>
public sealed class PdfSampleRendererTests : IDisposable
{
    private const string PackageFolder = "html";

    private const string ValidManifest = """
        {
          "manifestVersion": "1.0",
          "id": "invoice-energy",
          "version": "1.1.0",
          "owner": "billing-system",
          "shared": false,
          "engine": "scriban",
          "engineVersion": "7.5.1",
          "dataContract": { "version": "1.0", "profile": "header-items-v1", "schema": "schema.json" },
          "outputs": {
            "html": {
              "parts": {
                "documentStart": "html/document-start.html.sbn",
                "row": "html/row.html.sbn",
                "documentEnd": "html/document-end.html.sbn"
              },
              "styles": ["html/styles.css"]
            },
            "pdf": {
              "paper": "A4",
              "orientation": "portrait",
              "marginMm": { "top": 15, "right": 12, "bottom": 18, "left": 12 },
              "verification": { "anchors": { "header": ["$.title"], "item": ["$.p"] } }
            }
          }
        }
        """;

    private const string Sample = """{"contractVersion":"1.0","header":{"title":"Invoice"},"items":[{"p":"Hello"}]}""";

    private readonly string _root = Directory.CreateTempSubdirectory("sample-renderer-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task APackageThatDeclaresNoVerifiableFieldIsRefusedBeforeAnythingIsComposed()
    {
        var adapter = new FakeAdapter(Text());
        var printer = new FakePrinter(WriteUnreadable);
        var manifest = Variant(json => json["outputs"]!["pdf"]!.AsObject().Remove("verification"));

        var attempt = await Renderer(adapter, printer).RenderAsync(Request(manifest, Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.Verification, failure.Kind);
        Assert.Equal("anchors-undeclared", failure.Reason);
        Assert.Equal("template.yaml", failure.Part);
        Assert.Equal(0, adapter.Renders);
        Assert.Equal(0, printer.Calls);
    }

    [Fact]
    public async Task APackageThatDeclaresPageTotalsIsRefused()
    {
        var adapter = new FakeAdapter(Text());
        var manifest = Variant(json => json["outputs"]!["pdf"]!["pageTotals"] = new JsonObject { ["enabled"] = true });

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(manifest, Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal("page-accounting", failure.Reason);
        Assert.Equal("template.yaml", failure.Part);
        Assert.Equal(0, adapter.Renders);
    }

    [Fact]
    public async Task AnUnusablePageSettingIsARefusalNamingTheManifest()
    {
        var adapter = new FakeAdapter(Text());
        var manifest = Variant(json => json["outputs"]!["pdf"]!["paper"] = "A5");

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(manifest, Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(VerificationReasons.PageSettingsInvalid, failure.Reason);
        Assert.Equal("template.yaml", failure.Part);
    }

    [Fact]
    public async Task AnUndeclaredLocaleRendersUnderTheInvariantCultureAndUtc()
    {
        PresentationSettings? observed = null;
        var adapter = new FakeAdapter(Text(presentation => observed = presentation));
        var printer = new FakePrinter(Crash);

        await Renderer(adapter, printer).RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal(CultureInfo.InvariantCulture.Name, observed.Culture.Name);
        Assert.Equal(TimeZoneInfo.Utc.Id, observed.TimeZone.Id);
    }

    [Fact]
    public async Task ADeclaredLocaleIsThePackagesOwn()
    {
        PresentationSettings? observed = null;
        var adapter = new FakeAdapter(Text(presentation => observed = presentation));
        var declared = Variant(json => json["localeDefaults"] = new JsonObject
        {
            ["culture"] = "es-AR",
            ["timeZone"] = "America/Argentina/Buenos_Aires",
        });

        await Renderer(adapter, new FakePrinter(Crash)).RenderAsync(Request(declared, Sample), TestContext.Current.CancellationToken);

        Assert.NotNull(observed);
        Assert.Equal("es-AR", observed.Culture.Name);
        Assert.Equal("America/Argentina/Buenos_Aires", observed.TimeZone.Id);
    }

    [Fact]
    public async Task AnUnusableDeclaredLocaleIsARefusalNamingTheManifest()
    {
        var adapter = new FakeAdapter(Text());
        var declared = Variant(json => json["localeDefaults"] = new JsonObject
        {
            ["culture"] = "xx-NOWHERE",
            ["timeZone"] = "UTC",
        });

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(declared, Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.EngineError, failure.Kind);
        Assert.Equal("template.yaml", failure.Part);
    }

    [Fact]
    public async Task AnEngineErrorNamesThePartThatWasRendering()
    {
        var adapter = new FakeAdapter(Throwing(
            TemplatePart.Row,
            () => new TemplatePackageException("The row part failed to render: the value is odd.")));

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.EngineError, failure.Kind);
        Assert.Equal("html/row.html.sbn", failure.Part);
    }

    [Fact]
    public async Task AnEngineLimitKeepsTheEnginesOwnLimitWord()
    {
        var adapter = new FakeAdapter(Throwing(
            TemplatePart.Row,
            () => TemplateGuardException.Quota("engine-steps", "The row part passed the engine's engine-steps limit.")));

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.EngineLimit, failure.Kind);
        Assert.Equal("engine-steps", failure.Limit);
        Assert.Equal("html/row.html.sbn", failure.Part);
    }

    [Fact]
    public async Task TheOutputGuardIsItsOwnClass()
    {
        var adapter = new FakeAdapter(Throwing(
            TemplatePart.Row,
            () => TemplateGuardException.Quota("render-output", "The composed text of the block passed 33554432 bytes.")));

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.GuardOutput, failure.Kind);
        Assert.Equal("html/row.html.sbn", failure.Part);
    }

    [Fact]
    public async Task ARowThatPrintsNoTextIsAVerificationFailureBeforeThePrint()
    {
        var adapter = new FakeAdapter((part, _, writer, _) => writer.Write(
            part switch
            {
                TemplatePart.DocumentStart => "Invoice",
                TemplatePart.Row => "   ",
                _ => string.Empty,
            }));
        var printer = new FakePrinter(WriteUnreadable);

        var attempt = await Renderer(adapter, printer).RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.Verification, failure.Kind);
        Assert.Equal("row-without-text", failure.Reason);
        Assert.Equal("html/row.html.sbn", failure.Part);
        Assert.Equal(1, failure.RecordOrdinal);
        Assert.Equal(0, printer.Calls);
    }

    [Fact]
    public async Task ACompositionDeadlineIsUnavailableAndNeverARefusal()
    {
        var adapter = new FakeAdapter(Throwing(
            TemplatePart.Row,
            () => TemplateGuardException.Timeout("The composed text of the block was not produced within 60 s.")));

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<SampleAttempt.Unavailable>(attempt);
        Assert.Equal(SampleUnavailableCause.RenderTimeout, unavailable.Cause);
        Assert.Equal("html/row.html.sbn", unavailable.Part);
    }

    [Fact]
    public async Task ACrashIsUnavailableAndNeverARefusal()
    {
        var attempt = await Renderer(new FakeAdapter(Text()), new FakePrinter(Crash))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var unavailable = Assert.IsType<SampleAttempt.Unavailable>(attempt);
        Assert.Equal(SampleUnavailableCause.RenderCrash, unavailable.Cause);
        Assert.Null(unavailable.Part);
    }

    [Fact]
    public async Task APrintDeadlineIsUnavailable()
    {
        var attempt = await Renderer(new FakeAdapter(Text()), new FakePrinter(_ => throw new OperationCanceledException()))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        Assert.Equal(SampleUnavailableCause.RenderTimeout, Assert.IsType<SampleAttempt.Unavailable>(attempt).Cause);
    }

    [Fact]
    public async Task AnUnreadablePrintedPageIsAVerificationFailure()
    {
        var attempt = await Renderer(new FakeAdapter(Text()), new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.Verification, failure.Kind);
        Assert.Equal(VerificationReasons.Unreadable, failure.Reason);
        Assert.Equal("html/row.html.sbn", failure.Part);
    }

    [Fact]
    public async Task AnEmptyReportThatPrintsNothingIsTheEmptySampleClass()
    {
        var adapter = new FakeAdapter((part, _, writer, _) => writer.Write(part == TemplatePart.DocumentStart ? string.Empty : string.Empty));
        var printer = new FakePrinter(WriteUnreadable);

        var attempt = await Renderer(adapter, printer)
            .RenderAsync(Request(Manifest(ValidManifest), Sample) with { EmptySample = true }, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.EmptySample, failure.Kind);
        Assert.Equal("html/document-start.html.sbn", failure.Part);
        Assert.Equal(0, printer.Calls);
    }

    [Fact]
    public async Task ASampleOverTheBoundIsRefusedBeforeAnyEngineIsReached()
    {
        var adapter = new FakeAdapter(Text());
        var overBound = new StringBuilder("""{"contractVersion":"1.0","header":{"title":"Invoice"},"items":[""")
            .AppendJoin(',', Enumerable.Range(0, 2_001).Select(_ => """{"p":"x"}"""))
            .Append("]}")
            .ToString();

        var attempt = await Renderer(adapter, new FakePrinter(WriteUnreadable))
            .RenderAsync(Request(Manifest(ValidManifest), overBound), TestContext.Current.CancellationToken);

        var failure = Assert.IsType<SampleAttempt.Failed>(attempt).Failure;
        Assert.Equal(SampleFailureKind.OverBound, failure.Kind);
        Assert.Equal("sample.json", failure.Part);
        Assert.Equal(2_001, failure.Records);
        Assert.Equal(0, adapter.Prepares);
    }

    [Fact]
    public async Task AClassTheRendererCannotNameIsRethrown()
    {
        var adapter = new FakeAdapter(Throwing(
            TemplatePart.Row,
            () => new InvalidOperationException("the host did something unexpected")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Renderer(adapter, new FakePrinter(WriteUnreadable))
                .RenderAsync(Request(Manifest(ValidManifest), Sample), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThePreparedPackageIsNotParsedAgainForTheEmptyTwin()
    {
        var adapter = new FakeAdapter(Text());
        var renderer = Renderer(adapter, new FakePrinter(Crash));
        var request = Request(Manifest(ValidManifest), Sample);

        await renderer.RenderAsync(request, TestContext.Current.CancellationToken);
        await renderer.RenderAsync(request with { EmptySample = true }, TestContext.Current.CancellationToken);

        Assert.Equal(1, adapter.Prepares);
        Assert.Equal(10, adapter.Renders);
    }

    private PdfSampleRenderer Renderer(ITemplateEngineAdapter adapter, IPdfPrinter printer) =>
        new(
            new BlockComposer(TimeProvider.System),
            printer,
            new BlockVerifier(),
            new TemplateEngineAdapterRegistry([adapter]),
            new PreparedTemplateCache(),
            new SampleRenderMetrics(MeterIdentity.ForHost("reportservice-test")),
            TimeProvider.System,
            Options.Create(new SampleRenderOptions()),
            scratchRoot: Path.Combine(_root, "work"));

    private SampleRequest Request(TemplateManifest manifest, string sample) =>
        new(
            manifest.Id!,
            manifest.Version!,
            manifest,
            new FolderTemplatePackageFiles(_root),
            Encoding.UTF8.GetBytes(sample));

    /// <summary>The valid declaration with one change, written as the manifest reader reads it.</summary>
    private TemplateManifest Variant(Action<JsonObject> change)
    {
        var json = JsonNode.Parse(ValidManifest)!.AsObject();
        change(json);
        return Manifest(json.ToJsonString());
    }

    private TemplateManifest Manifest(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, PackageFolder));
        File.WriteAllText(Path.Combine(_root, PackageFolder, "styles.css"), "body { font-family: \"Liberation Sans\", sans-serif; }");
        File.WriteAllText(Path.Combine(_root, "schema.json"), "{}");
        return TemplateManifestJson.ReadTyped(json);
    }

    private static Action<TemplatePart, RenderContext, GuardedWriter, PresentationSettings> Text(Action<PresentationSettings>? observed = null) =>
        (part, _, writer, presentation) =>
        {
            observed?.Invoke(presentation);
            writer.Write(part switch
            {
                TemplatePart.DocumentStart => "Invoice",
                TemplatePart.Row => "Hello",
                _ => string.Empty,
            });
        };

    /// <summary>The same text, with one part that raises what the case names.</summary>
    private static Action<TemplatePart, RenderContext, GuardedWriter, PresentationSettings> Throwing(TemplatePart failing, Func<Exception> exception)
    {
        var text = Text();
        return (part, context, writer, presentation) =>
        {
            if (part == failing)
            {
                throw exception();
            }

            text(part, context, writer, presentation);
        };
    }

    private static void WriteUnreadable(string pdfPath) => File.WriteAllText(pdfPath, "not a pdf");

    private static void Crash(string pdfPath) => throw new PrintTemporarilyFailedException("the browser went away");

    /// <summary>An engine whose parts are scripted, so one case reaches exactly one boundary.</summary>
    private sealed class FakeAdapter(Action<TemplatePart, RenderContext, GuardedWriter, PresentationSettings> render) : ITemplateEngineAdapter
    {
        public int Prepares { get; private set; }

        public int Renders { get; private set; }

        public string Engine => TemplateEngineNames.Scriban;

        public string Version => "7.5.1";

        public PreparedTemplate Prepare(RenderManifest manifest, ITemplatePackageFiles files)
        {
            Prepares++;
            return new PreparedTemplate(
                manifest,
                manifest.Parts.ToDictionary(
                    entry => entry.Key,
                    _ => new PreparedPart(new object(), string.Empty)));
        }

        public void RenderPart(
            PreparedTemplate template,
            TemplatePart part,
            RenderContext context,
            PresentationSettings presentation,
            GuardedWriter writer,
            CancellationToken cancellationToken)
        {
            Renders++;
            render(part, context, writer, presentation);
        }
    }

    /// <summary>A printer that writes a document it cannot claim is a PDF, or one that fails as the case names.</summary>
    private sealed class FakePrinter(Action<string> print) : IPdfPrinter
    {
        public int Calls { get; private set; }

        public Task<PrintOutcome> PrintAsync(
            string document,
            PageRule pageRule,
            ITemplatePackageFiles files,
            string pdfPath,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            print(pdfPath);
            return Task.FromResult(new PrintOutcome(pdfPath, 0, 0, TimeSpan.FromMilliseconds(1)));
        }
    }
}
