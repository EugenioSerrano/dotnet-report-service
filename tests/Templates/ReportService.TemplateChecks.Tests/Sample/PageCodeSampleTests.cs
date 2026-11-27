using System.Text;
using System.Text.Json.Nodes;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;
using ReportService.TemplateChecks.Sample;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplateSource.Checks;

namespace ReportService.TemplateChecks.Tests.Sample;

/// <summary>
/// The populated sample carries the declared per-page code: every page yields a value,
/// prints nothing under the code's area, and a one-part assembly of the sample passes
/// the assembly verification without a numbering contract. Each failure is a
/// <see cref="SampleFailureKind.PageCode"/> naming the page, and the sample render
/// check refuses it as TEMPLATE_INVALID on the manifest with a fix for its reason.
/// </summary>
public sealed class PageCodeSampleTests
{
    private static readonly PageFrame Frame = PageFrame.FromMillimetres(210, 297, 15, 12, 18, 12);

    [Fact]
    public async Task APrintedSampleWhosePagesYieldAValueCarriesTheCodeOnEveryPage()
    {
        var (failure, pages) = await CheckAsync(Pdf("Invoice INV-00000001", "Invoice INV-00000002"));

        Assert.Null(failure);
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task APageWithoutAValueIsNamed()
    {
        var (failure, _) = await CheckAsync(Pdf("Invoice INV-00000001", "a page without an invoice"));

        Assert.Equal(SampleFailureKind.PageCode, failure!.Kind);
        Assert.Equal(PageCodeSample.Reasons.NoValue, failure.Reason);
        Assert.Equal(2, failure.PageNumber);
    }

    [Fact]
    public async Task TemplateTextUnderTheCodeAreaIsNamed()
    {
        var (failure, _) = await CheckAsync(Pdf(["Invoice INV-00000001"], underCode: "hidden"));

        Assert.Equal(PageCodeSample.Reasons.TextInCodeArea, failure!.Reason);
        Assert.Equal(1, failure.PageNumber);
    }

    [Fact]
    public async Task AGeneratorThatRefusesOrDrawsDisallowedOrOversizedContentIsNamed()
    {
        Assert.Equal(
            PageCodeSample.Reasons.GeneratorRefused,
            (await CheckAsync(Pdf("Invoice INV-00000001"), new Refusing())).Failure!.Reason);
        Assert.Equal(
            PageCodeSample.Reasons.ContentNotAllowed,
            (await CheckAsync(Pdf("Invoice INV-00000001"), new Fixed(Encoding.ASCII.GetBytes("BT ET")))).Failure!.Reason);
        Assert.Equal(
            PageCodeSample.Reasons.TooLarge,
            (await CheckAsync(Pdf("Invoice INV-00000001"), new Fixed(Noisy()))).Failure!.Reason);
    }

    [Fact]
    public async Task AnUnregisteredGeneratorIsRefused()
    {
        var (failure, _) = await CheckAsync(Pdf("Invoice INV-00000001"), registry: new PageCodeGeneratorRegistry([]));

        Assert.Equal(PageCodeSample.Reasons.GeneratorRefused, failure!.Reason);
    }

    [Fact]
    public async Task ANonDeterministicGeneratorFailsTheVerification()
    {
        var (failure, _) = await CheckAsync(Pdf("Invoice INV-00000001"), new Drifting());

        Assert.StartsWith(PageCodeSample.Reasons.Verification, failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterPrintChecksOnlyThePopulatedSampleOfAVersionThatDeclaresACode()
    {
        var pdf = Pdf("Invoice INV-00000001");
        var generator = new ReferenceGridPageCodeGenerator();
        var registry = new PageCodeGeneratorRegistry([
            new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference)),
        ]);
        var work = Path.Combine(Path.GetDirectoryName(pdf)!, "after");
        var token = TestContext.Current.CancellationToken;
        var declared = Request(Manifest(generator.Name), empty: false);

        Assert.Equal((null, 1), await PageCodeSample.AfterPrintAsync(declared, registry, pdf, Frame, work, token));
        Assert.Equal((null, null), await PageCodeSample.AfterPrintAsync(declared with { EmptySample = true }, registry, pdf, Frame, work, token));
        var undeclared = Request(TemplatePackages.Manifest.TemplateManifestJson.ReadTyped(Packages.ManifestJson()), empty: false);
        Assert.Equal((null, null), await PageCodeSample.AfterPrintAsync(undeclared, registry, pdf, Frame, work, token));

        var (failure, pages) = await PageCodeSample.AfterPrintAsync(declared, registry: null, pdf, Frame, work, token);
        Assert.Equal(PageCodeSample.Reasons.GeneratorRefused, failure!.Reason);
        Assert.Null(pages);
    }

    [Fact]
    public void TheOutcomeDiscardsTheSampleOnAFailureAndBuildsTheRenderOtherwise()
    {
        var discarded = 0;
        var rendered = new SampleAttempt.Rendered(new SampleFacts(1, 1, 1, 1, 0, 0, 0));
        var failure = new SampleFailure(SampleFailureKind.PageCode, "template.yaml", Reason: "no-value", PageNumber: 1);

        Assert.Same(rendered, PageCodeSample.Outcome(null, () => discarded++, () => rendered));
        Assert.Equal(0, discarded);
        var refused = Assert.IsType<SampleAttempt.Failed>(PageCodeSample.Outcome(failure, () => discarded++, () => rendered));
        Assert.Same(failure, refused.Failure);
        Assert.Equal(1, discarded);
    }

    private static SampleRequest Request(TemplateManifest manifest, bool empty) =>
        new("invoice-energy", "1.1.0", manifest, new NoFiles(), ReadOnlyMemory<byte>.Empty, EmptySample: empty);

    private sealed class NoFiles : ReportService.Templates.ITemplatePackageFiles
    {
        public bool Exists(string path) => false;

        public Stream OpenRead(string path) => throw new FileNotFoundException(path);

        public string ReadAllText(string path) => throw new FileNotFoundException(path);
    }

    [Theory]
    [InlineData(PageCodeSample.Reasons.TextInCodeArea, "free of template text")]
    [InlineData(PageCodeSample.Reasons.NoValue, "every page prints")]
    [InlineData(PageCodeSample.Reasons.GeneratorRefused, "generator's options")]
    [InlineData(PageCodeSample.Reasons.ContentNotAllowed, "paths and fills")]
    [InlineData(PageCodeSample.Reasons.TooLarge, "smaller code")]
    [InlineData("verification:code-object", "every page of the sample carries")]
    public async Task TheSampleRenderCheckRefusesAPageCodeFailureWithItsFix(string reason, string fix)
    {
        var renderer = new FailingRenderer(new SampleFailure(
            SampleFailureKind.PageCode,
            "template.yaml",
            Reason: reason,
            PageNumber: 3));
        using var package = Packages.Package();

        var result = await new SampleRenderCheck(renderer).CheckAsync(
            package.Candidate(Packages.ManifestJson()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Equal(TemplateCheckCodes.TemplateInvalid, result.Code);
        Assert.Contains("outputs.pdf.pageCode cannot be drawn on page 3", result.Reason, StringComparison.Ordinal);
        Assert.Contains(fix, result.Reason, StringComparison.Ordinal);
    }

    private static Task<(SampleFailure? Failure, int CodePages)> CheckAsync(
        string pdf,
        IPageCodeGenerator? generator = null,
        PageCodeGeneratorRegistry? registry = null)
    {
        generator ??= new ReferenceGridPageCodeGenerator();
        var manifest = Manifest(generator.Name);
        registry ??= new PageCodeGeneratorRegistry([
            new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference)),
        ]);
        var work = Path.Combine(Path.GetDirectoryName(pdf)!, "work");
        return PageCodeSample.CheckAsync(manifest, registry, pdf, Frame, work, TestContext.Current.CancellationToken);
    }

    private static TemplateManifest Manifest(string generator)
    {
        var root = JsonNode.Parse(Packages.ManifestJson())!;
        root["outputs"]!["pdf"]!["pageCode"] = new JsonObject
        {
            ["generator"] = generator,
            ["area"] = new JsonObject { ["leftMm"] = 160, ["topMm"] = 20, ["widthMm"] = 25, ["heightMm"] = 25 },
            ["source"] = new JsonObject
            {
                ["value"] = "{page.number}/{page.total}|{match}",
                ["match"] = new JsonObject { ["pattern"] = "INV-[0-9]{8}", ["maxLength"] = 12 },
            },
        };
        return TemplatePackages.Manifest.TemplateManifestJson.ReadTyped(root.ToJsonString());
    }

    private static string Pdf(params string[] pages) => Pdf(pages, underCode: null);

    private static string Pdf(string[] pages, string? underCode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "page-code-sample-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.pdf");
        var face = Face();
        using (var document = new PdfDocument())
        {
            var font = new XFont(face, 10, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            foreach (var text in pages)
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(Frame.WidthPt);
                page.Height = XUnit.FromPoint(Frame.HeightPt);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString(text, font, XBrushes.Black, new XRect(40, 60, 300, 20), XStringFormats.TopLeft);
                if (underCode is not null)
                {
                    var left = 160 * PageFrame.PointsPerMillimetre;
                    var top = 20 * PageFrame.PointsPerMillimetre;
                    graphics.DrawString(underCode, font, XBrushes.Black, new XRect(left + 10, top + 10, 40, 12), XStringFormats.TopLeft);
                }
            }

            document.Save(path);
        }

        return path;
    }

    private static string Face()
    {
        var resolver = PackagedFontResolver.Instance;
        try
        {
            GlobalFontSettings.FontResolver = resolver;
        }
        catch (InvalidOperationException)
        {
            // A resolver installed earlier in the process is the same packaged one.
        }

        var file = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
        return resolver.Register(new NumberingFont("Liberation Sans", "fonts/LiberationSans-Regular.ttf", 10, File.ReadAllBytes(file)));
    }

    private static byte[] Noisy()
    {
        var random = new Random(5);
        var builder = new StringBuilder("q ");
        while (builder.Length < 60_000)
        {
            builder.Append(random.Next(100000, 999999)).Append(' ').Append(random.Next(100000, 999999)).Append(" 1 1 re ");
        }

        return Encoding.ASCII.GetBytes(builder.Append("f Q").ToString());
    }

    private sealed class Fixed(byte[] content) : IPageCodeGenerator
    {
        public string Name => "fixed";

        public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
            PageCodeCheck.Accept(content);

        public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
            PageCodeContent.Of(content);
    }

    private sealed class Refusing : IPageCodeGenerator
    {
        public string Name => "refusing";

        public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
            PageCodeCheck.Refuse("never", "never");

        public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
            PageCodeContent.Refused("never");
    }

    // A generator that draws something different each time it is asked: the assembly's
    // drawing and the verification's reproduction can never agree.
    private sealed class Drifting : IPageCodeGenerator
    {
        private int _calls;

        public string Name => "drifting";

        public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
            PageCodeCheck.Accept(Encoding.ASCII.GetBytes("q 0 0 1 1 re f Q"));

        public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
            PageCodeContent.Of(Encoding.ASCII.GetBytes($"q 0 0 {++_calls} 1 re f Q"));
    }

    private sealed class FailingRenderer(SampleFailure failure) : ISampleRenderer
    {
        public Task<SampleAttempt> RenderAsync(SampleRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<SampleAttempt>(new SampleAttempt.Failed(failure));
    }
}
