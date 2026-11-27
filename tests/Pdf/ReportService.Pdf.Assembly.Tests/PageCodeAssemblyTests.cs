using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.Pdf.Assembly.Tests;

public sealed class PageCodeAssemblyTests
{
    private const string Value = "{page.number}/{page.total}|{match}";
    private const string Pattern = "part[0-9]+page[0-9]+start";

    private static readonly PageCodeRectangle CodeArea = new(160, 100, 20, 20);

    [Fact]
    public async Task EveryPageOfAMultiPartDocumentCarriesTheCodeOfItsOwnValueBesideItsNumber()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[]
        {
            AssemblyFixture.CreatePart(directory, 0, 2),
            AssemblyFixture.CreatePart(directory, 1, 1),
        };
        var code = Code();
        var output = Path.Combine(directory, "out.pdf");

        var result = await Assemble(parts, directory, output, AssemblyFixture.Contract(), code);

        Assert.Equal(3, result.PageCount);
        Assert.Equal(3, result.Metrics.CodePages);
        Assert.Equal(ReferenceGridPageCodeGenerator.GeneratorName, result.Metrics.CodeGenerator!.Name);
        var reading = PdfWordReader.ReadDocument(output, AssemblyFixture.Frame.Margins);
        var expectedValues = new[] { "1/3|part0page1start", "2/3|part0page2start", "3/3|part1page1start" };
        for (var page = 0; page < 3; page++)
        {
            var form = Assert.Single(reading.Pages[page].Graphics!.Forms, candidate => candidate.Name == PageCodeFormWriter.ResourceName);
            var expected = new ReferenceGridPageCodeGenerator()
                .Generate(expectedValues[page], new Dictionary<string, string>(), CodeArea.Size).Content!;
            Assert.Equal(expected, form.Content);
            Assert.Equal(CodeArea.Region.WidthPt, form.BoundingBox![2], 3);
        }

        Assert.Equal(["Page 1 of 3", "Page 2 of 3", "Page 3 of 3"], Stamps(reading));
        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), code).Passed);
    }

    [Fact]
    public async Task TheFormStreamDecodesToExactlyTheGeneratorsBytes()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var output = Path.Combine(directory, "out.pdf");
        var supplied = Encoding.ASCII.GetBytes("q\n0 g\n1.5 2.25 3 4 re\nf\nQ\n");
        var code = Code(generator: new FixedGenerator("fixed", supplied));

        await Assemble(parts, directory, output, AssemblyFixture.Contract(), code);

        var form = Assert.Single(PdfWordReader.ReadDocument(output, AssemblyFixture.Frame.Margins).Pages[0].Graphics!.Forms);
        Assert.Equal(supplied, form.Content);
    }

    [Fact]
    public async Task AOnePartAssemblyDrawsTheCodeAndNoNumberAndVerifiesWithoutANumberingContract()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };
        var code = Code(numbering: null);
        var output = Path.Combine(directory, "out.pdf");

        var result = await Assemble(parts, directory, output, numbering: null, code);

        Assert.Equal(2, result.Metrics.CodePages);
        Assert.Equal(AssemblyFixture.PageTexts(parts[0].Path), AssemblyFixture.PageTexts(output));
        var reading = PdfWordReader.ReadDocument(output, AssemblyFixture.Frame.Margins);
        Assert.All(reading.Pages, page => Assert.False(page.Graphics!.ResourceNames.ContainsKey("Font") && page.Graphics.ResourceNames["Font"].Count > 1));
        Assert.True(Verify(output, parts, numbering: null, code).Passed);
    }

    [Fact]
    public async Task AOnePartVerificationFailsWhenAStampWasAdded()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var code = Code(numbering: null);
        var output = Path.Combine(directory, "out.pdf");

        // Assembled with a numbering stamp, verified as a code-only one-part document.
        await Assemble(parts, directory, output, AssemblyFixture.Contract(), Code());

        var verdict = Verify(output, parts, numbering: null, code);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reason, new[] { AssemblyFailureReasons.Order, AssemblyFailureReasons.Resources });
    }

    [Fact]
    public async Task AOnePartVerificationFailsWhenThePartsOwnTextWasAltered()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var code = Code(numbering: null);
        var output = Path.Combine(directory, "out.pdf");
        await Assemble(parts, directory, output, numbering: null, code);
        var altered = AssemblyFixture.Rebuild(output, Path.Combine(directory, "altered.pdf"), _ => true, document => DrawText(document.Pages[0], "extra", 40, 300));

        var verdict = Verify(altered, parts, numbering: null, code);

        Assert.Equal(AssemblyFailureReasons.Order, verdict.Reason);
    }

    [Fact]
    public async Task NoNumberingIsRefusedForTwoPartsOrWithoutACode()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1), AssemblyFixture.CreatePart(directory, 1, 1) };
        var output = Path.Combine(directory, "out.pdf");

        await Assert.ThrowsAsync<ArgumentException>(() => Assemble(parts, directory, output, numbering: null, Code(numbering: null)));
        await Assert.ThrowsAsync<ArgumentException>(() => Assemble([parts[0]], directory, output, numbering: null, code: null));
        Assert.Throws<ArgumentException>(() => Verify(output, parts, numbering: null, Code(numbering: null)));
        Assert.Throws<ArgumentException>(() => Verify(output, [parts[0]], numbering: null, code: null));
    }

    [Fact]
    public async Task APageWhoseSourceYieldsNoValueFailsTheAssemblyAndDrawsNoBlankCode()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };
        var code = Code(pattern: "INV-[0-9]{8}");

        var refusal = await Assert.ThrowsAsync<AssemblyInputException>(
            () => Assemble(parts, directory, Path.Combine(directory, "out.pdf"), AssemblyFixture.Contract(), code));

        Assert.Contains("no value", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFormLargerThanTheStoredBoundFailsTheAssembly()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var code = Code(generator: new FixedGenerator("noisy", NoisyContent(60 * 1024)));

        var refusal = await Assert.ThrowsAsync<AssemblyInputException>(
            () => Assemble(parts, directory, Path.Combine(directory, "out.pdf"), AssemblyFixture.Contract(), code));

        Assert.Contains("stored bound", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentWithATextOperatorFailsTheAssembly()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var code = Code(generator: new FixedGenerator("texty", Encoding.ASCII.GetBytes("BT (x) Tj ET\n")));

        var refusal = await Assert.ThrowsAsync<AssemblyInputException>(
            () => Assemble(parts, directory, Path.Combine(directory, "out.pdf"), AssemblyFixture.Contract(), code));

        Assert.Contains("not allowed", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReferenceGeneratorsWorstCaseFitsTheStoredBound()
    {
        var check = new ReferenceGridPageCodeGenerator()
            .Validate(new Dictionary<string, string>(), CodeArea.Size, PageCodeSource.MaximumValueBytes);

        Assert.True(check.Accepted);
        Assert.Null(PageCodeContentValidator.Problem(check.WorstCaseContent));
        Assert.True(PageCodeFormWriter.StoredSize(check.WorstCaseContent!, CodeArea.Size) <= PageCodeContentValidator.MaximumStoredBytes);
        Assert.True(PageCodeFormWriter.StoredSize(NoisyContent(60 * 1024), CodeArea.Size) > PageCodeContentValidator.MaximumStoredBytes);
    }

    [Fact]
    public async Task AMissingCodeIsCaught()
    {
        var (directory, parts, output) = await CodedDocument(withCode: false);

        Assert.Equal(AssemblyFailureReasons.CodeMissing, Verify(output, parts, AssemblyFixture.Contract(), Code()).Reason);
        _ = directory;
    }

    [Fact]
    public async Task ACodeDeclaredElsewhereThanWhereItWasDrawnIsCaught()
    {
        var (_, parts, output) = await CodedDocument();

        var moved = Code(area: CodeArea with { TopMm = 150 });
        Assert.Equal(AssemblyFailureReasons.CodePlacement, Verify(output, parts, AssemblyFixture.Contract(), moved).Reason);
    }

    [Fact]
    public async Task ACodeOverlappingTheNumberingAreaIsCaught()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var output = Path.Combine(directory, "out.pdf");
        var numbering = AssemblyFixture.Contract(new NumberingArea(165, 105, 30, 5));
        await Assemble(parts, directory, output, numbering, Code(numbering: numbering.Area));

        Assert.Equal(AssemblyFailureReasons.CodePlacement, Verify(output, parts, numbering, Code(numbering: numbering.Area)).Reason);
    }

    [Fact]
    public async Task AnotherPagesCodeObjectIsCaught()
    {
        var (_, parts, output) = await CodedDocument();

        // Verified against a source that gives every page another value, the drawn
        // objects are other pages' objects.
        var other = Code(value: "{page.total}/{page.number}|{match}");
        Assert.Equal(AssemblyFailureReasons.CodeObject, Verify(output, parts, AssemblyFixture.Contract(), other).Reason);
    }

    [Fact]
    public async Task AnObjectAddedBesideTheCodeIsCaught()
    {
        var (directory, parts, output) = await CodedDocument();
        var mutated = AssemblyFixture.Rebuild(output, Path.Combine(directory, "extra.pdf"), _ => true, document =>
        {
            var extra = new PdfDictionary(document);
            extra.Elements.SetName("/Type", "/XObject");
            extra.Elements.SetName("/Subtype", "/Form");
            extra.Elements["/BBox"] = new PdfArray(document, new PdfReal(0), new PdfReal(0), new PdfReal(5), new PdfReal(5));
            extra.CreateStream(Encoding.ASCII.GetBytes("q Q\n"));
            document.Internals.AddObject(extra);
            var resources = document.Pages[0].Elements.GetDictionary("/Resources")!;
            resources.Elements.GetDictionary("/XObject")!.Elements["/Extra"] = extra.Reference;
        });

        Assert.Equal(AssemblyFailureReasons.CodeUnexpected, Verify(mutated, parts, AssemblyFixture.Contract(), Code()).Reason);
    }

    [Fact]
    public async Task TextAddedInsideTheCodeAreaIsCaught()
    {
        var (directory, parts, output) = await CodedDocument();
        var region = CodeArea.Region;
        var mutated = AssemblyFixture.Rebuild(output, Path.Combine(directory, "text.pdf"), _ => true, document =>
            DrawText(document.Pages[0], "inside", region.LeftPt + 5, region.TopPt + 5));

        Assert.Equal(AssemblyFailureReasons.CodeText, Verify(mutated, parts, AssemblyFixture.Contract(), Code()).Reason);
    }

    [Fact]
    public async Task TextTheSourcePagePrintedUnderTheCodeAreaPassesWhenUnchanged()
    {
        var directory = AssemblyFixture.NewDirectory();
        var region = CodeArea.Region;
        var parts = new[]
        {
            AssemblyFixture.CreatePart(directory, 0, 1, decorate: page => DrawText(page, "under", region.LeftPt + 5, region.TopPt + 5)),
        };
        var output = Path.Combine(directory, "out.pdf");
        await Assemble(parts, directory, output, AssemblyFixture.Contract(), Code());

        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), Code()).Passed);
    }

    [Fact]
    public async Task ACodeOutsideThePrintableBoxIsCaught()
    {
        // Drawn at 200 mm from the left: on the page, but inside the 10 mm right margin.
        var outside = new PageCodeRectangle(195, 100, 10, 10);
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 1) };
        var output = Path.Combine(directory, "out.pdf");
        await Assemble(parts, directory, output, AssemblyFixture.Contract(), Code(area: outside));

        var printable = AssemblyPageCode.PrintableBoxOf(AssemblyFixture.Frame);
        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), Code(area: outside)).Passed);
        Assert.Equal(
            AssemblyFailureReasons.CodePlacement,
            Verify(output, parts, AssemblyFixture.Contract(), Code(area: outside, printable: printable)).Reason);
        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), Code(area: outside, printable: new PageCodeText.Region(0, 0, 600, 850))).Passed);
    }

    [Fact]
    public async Task ACodeOverTheAccountingFooterIsCaught()
    {
        var (_, parts, output) = await CodedDocument();
        var region = CodeArea.Region;
        var footer = new PageCodeText.Region(0, region.TopPt + 5, 600, 20);

        Assert.Equal(
            AssemblyFailureReasons.CodePlacement,
            Verify(output, parts, AssemblyFixture.Contract(), Code(footer: footer)).Reason);
        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), Code(footer: new PageCodeText.Region(0, 800, 600, 20))).Passed);
    }

    [Fact]
    public void ARegionDeclaredInMillimetresIsInPointsAndKnowsItsContainer()
    {
        var region = PageCodeText.Region.FromMillimetres(10, 20, 30, 40);

        Assert.Equal(10 * 72 / 25.4, region.LeftPt, 6);
        Assert.Equal(40 * 72 / 25.4, region.HeightPt, 6);
        Assert.True(region.Within(PageCodeText.Region.FromMillimetres(0, 0, 50, 70)));
        Assert.False(region.Within(PageCodeText.Region.FromMillimetres(15, 0, 50, 70)));
    }

    [Fact]
    public async Task AFormWithAnotherBoundingBoxIsNotCountedAsTheCode()
    {
        var (_, parts, output) = await CodedDocument();

        var wider = Code(area: CodeArea with { WidthMm = 30 });
        Assert.Equal(AssemblyFailureReasons.CodeMissing, Verify(output, parts, AssemblyFixture.Contract(), wider).Reason);
    }

    [Fact]
    public async Task ADocumentWithoutADeclarationVerifiesAsBefore()
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2) };
        var output = Path.Combine(directory, "out.pdf");
        await AssemblyFixture.AssembleAsync(parts, directory, output);

        Assert.True(Verify(output, parts, AssemblyFixture.Contract(), code: null).Passed);
    }

    private static async Task<(string Directory, AssemblyPart[] Parts, string Output)> CodedDocument(bool withCode = true)
    {
        var directory = AssemblyFixture.NewDirectory();
        var parts = new[] { AssemblyFixture.CreatePart(directory, 0, 2), AssemblyFixture.CreatePart(directory, 1, 1) };
        var output = Path.Combine(directory, "out.pdf");
        await Assemble(parts, directory, output, AssemblyFixture.Contract(), withCode ? Code() : null);
        return (directory, parts, output);
    }

    internal static AssemblyPageCode Code(
        IPageCodeGenerator? generator = null,
        string value = Value,
        string pattern = Pattern,
        PageCodeRectangle? area = null,
        NumberingArea? numbering = null,
        PageCodeText.Region? printable = null,
        PageCodeText.Region? footer = null)
    {
        generator ??= new ReferenceGridPageCodeGenerator();
        var problems = new List<PageCodeProblem>();
        var source = PageCodeSource.Parse(value, value.Contains("{match}", StringComparison.Ordinal) ? pattern : null, value.Contains("{match}", StringComparison.Ordinal) ? 20 : null, problems)
            ?? throw new InvalidOperationException(string.Join("; ", problems.Select(problem => problem.Problem)));
        var declaration = new PageCodeDeclaration(generator.Name, new Dictionary<string, string>(), area ?? CodeArea, source);
        return new AssemblyPageCode(
            declaration,
            new RegisteredPageCodeGenerator(generator, PageCodeGeneratorDescriptor.Of(generator, PageCodeGeneratorOrigins.Reference)),
            "invoices",
            "1.0.0",
            AssemblyFixture.Frame.Margins,
            numbering ?? AssemblyFixture.Area,
            printable,
            footer);
    }

    private static Task<AssemblyResult> Assemble(
        IReadOnlyList<AssemblyPart> parts,
        string directory,
        string output,
        PageNumbering? numbering,
        AssemblyPageCode? code) =>
        new PdfSharpAssembler().AssembleAsync(
            new AssemblyRequest(parts, numbering, output, directory, PageCode: code),
            CancellationToken.None);

    private static AssemblyVerdict Verify(string output, IEnumerable<AssemblyPart> parts, PageNumbering? numbering, AssemblyPageCode? code) =>
        AssemblyVerifier.Verify(new AssemblyVerificationRequest(
            output,
            AssemblyFixture.Expectations(parts),
            AssemblyFixture.Frame,
            numbering,
            code));

    private static IReadOnlyList<string> Stamps(PdfDocumentReading reading)
    {
        var area = AssemblyFixture.Area;
        var region = new PageCodeText.Region(area.LeftPt, area.TopPt, area.WidthPt, area.HeightPt);
        return [.. reading.Pages.Select(page => string.Join(' ', PageCodeText.Inside(page.Page, region)))];
    }

    private static void DrawText(PdfPage page, string text, double left, double top)
    {
        var font = new XFont(AssemblyFixture.Face(), 8, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        using var graphics = XGraphics.FromPdfPage(page);
        graphics.DrawString(text, font, XBrushes.Black, new XRect(left, top, 100, 10), XStringFormats.TopLeft);
    }

    internal static byte[] NoisyContent(int bytes)
    {
        var random = new Random(7);
        var builder = new StringBuilder("q\n");
        while (builder.Length < bytes - 16)
        {
            builder.Append(string.Create(CultureInfo.InvariantCulture, $"{random.NextDouble() * 1000:0.000000} {random.NextDouble() * 1000:0.000000} 1 1 re\n"));
        }

        builder.Append("f\nQ\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    internal sealed class FixedGenerator(string name, byte[] content) : IPageCodeGenerator
    {
        public string Name => name;

        public PageCodeCheck Validate(IReadOnlyDictionary<string, string> options, PageCodeArea area, int maxValueBytes) =>
            PageCodeCheck.Accept(content);

        public PageCodeContent Generate(string value, IReadOnlyDictionary<string, string> options, PageCodeArea area) =>
            PageCodeContent.Of(content);
    }
}
