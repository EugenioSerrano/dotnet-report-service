using System.Security.Cryptography;
using ReportService.Pdf.Verification;
using ReportService.Pdf.Verification.PageCodes;

namespace ReportService.PdfStructure.Tests;

/// <summary>
/// The committed coded golden: a document the product's assembler wrote from two
/// parts (two pages and one page), stamped "Page X of Y" and carrying a per-page code
/// drawn by the reference generator from "{page.number}/{page.total}|{match}", the
/// match being the first eight digits of the invoice number each page prints. It is read in-process and held to the
/// structure the assembly verification requires: one code form per page, its box the
/// declared area, drawn once onto the area, its decoded content the generator's for
/// the page's value - computed here from the page's known text, never read from the
/// code - and the numbering continuous. The strict structural validator's exemption
/// rule, which an assembled document needs, is held to its exact shape below.
/// </summary>
public sealed class CodedGoldenTests
{
    private const string Golden = "coded-two-parts.pdf";
    private static readonly PageCodeRectangle Area = new(160, 100, 20, 20);

    private static readonly string[] Values = ["1/3|INV-00000000", "2/3|INV-00000000", "3/3|INV-00000001"];

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Goldens", "Coded", Golden);

    [Fact]
    public void TheGoldenIsTheCommittedOne() =>
        Assert.Equal(Sums()[Golden], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path))));

    [Fact]
    public void EveryPageCarriesOneCodeOfItsOwnValueInsideTheArea()
    {
        var reading = PdfDocumentReader.Read(Path, PageFrame.FromMillimetres(210, 297, 12, 10, 12, 10).Margins);
        var generator = new ReferenceGridPageCodeGenerator();
        var region = Area.Region;

        Assert.Equal(3, reading.Pages.Count);
        for (var index = 0; index < 3; index++)
        {
            var page = reading.Pages[index];
            var form = Assert.Single(page.Graphics!.Forms);
            Assert.Equal([0d, 0d, Math.Round(region.WidthPt, 4), Math.Round(region.HeightPt, 4)], form.BoundingBox!.Select(value => Math.Round(value, 4)));
            Assert.Equal(generator.Generate(Values[index], new Dictionary<string, string>(), Area.Size).Content, form.Content);
            Assert.Null(PageCodeContentValidator.Problem(form.Content));
            var drawing = Assert.Single(page.Graphics.Drawings);
            Assert.Equal(form.Name, drawing.Name);
            Assert.Equal(region.LeftPt, drawing.Matrix[4], 3);
            Assert.Equal(page.Page.Height - region.TopPt - region.HeightPt, drawing.Matrix[5], 3);
            Assert.Empty(PageCodeText.Inside(page.Page, region));
            Assert.Contains($"Page {index + 1} of 3", string.Join(' ', page.Page.Words.Select(word => word.Text)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AClean0IsAPass()
    {
        var verdict = StructuralValidator.Classify(Golden, 0, "checking coded-two-parts.pdf\nPDF Version: 1.7\nNo syntax or stream encoding errors found; the file may still contain\nerrors that qpdf cannot detect\n", string.Empty);

        Assert.True(verdict.Passed);
    }

    [Fact]
    public void ExitThreeWithOnlyTheToleratedMessageIsAnExemptPass()
    {
        var verdict = StructuralValidator.Classify(Golden, 3, Out(), Warning(12) + Warning(240) + StructuralValidator.ClosingLine + "\n");

        Assert.True(verdict.Passed);
        Assert.Equal(2, verdict.ExemptWarnings);
    }

    [Theory]
    [InlineData("WARNING: coded-two-parts.pdf (offset 12): stream will be re-encoded\n", "other warning")]
    [InlineData("WARNING: other.pdf (offset 12): input stream is complete but output may still be valid\n", "other warning")]
    [InlineData("ERROR: coded-two-parts.pdf: unexpected end\n", "error line")]
    public void AnyOtherWarningOrAnErrorFails(string line, string reason)
    {
        var verdict = StructuralValidator.Classify(Golden, 3, Out(), Warning(12) + line + StructuralValidator.ClosingLine + "\n");

        Assert.False(verdict.Passed);
        Assert.StartsWith(reason, verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShapeIsExact()
    {
        Assert.False(StructuralValidator.Classify(Golden, 3, Out(), StructuralValidator.ClosingLine + "\n").Passed);
        Assert.False(StructuralValidator.Classify(Golden, 3, Out(), Warning(12)).Passed);
        Assert.False(StructuralValidator.Classify(Golden, 3, Out(), StructuralValidator.ClosingLine + "\n" + Warning(12)).Passed);
        Assert.False(StructuralValidator.Classify(Golden, 3, "checking another.pdf\n", Warning(12) + StructuralValidator.ClosingLine).Passed);
        Assert.False(StructuralValidator.Classify(Golden, 3, Out() + Warning(12), StructuralValidator.ClosingLine).Passed);
        Assert.False(StructuralValidator.Classify(Golden, 2, Out(), "qpdf: coded-two-parts.pdf: not a PDF file\n").Passed);
        Assert.False(StructuralValidator.Classify(Golden, 0, Out(), Warning(12)).Passed);
    }

    private static string Out() => "checking " + Golden + "\nPDF Version: 1.7\n";

    private static string Warning(int offset) =>
        $"WARNING: {Golden} (object 7 0, offset {offset}): {StructuralValidator.ExemptTail}\n";

    private static Dictionary<string, string> Sums() =>
        File.ReadAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Goldens", "Coded", "SHA256SUMS"))
            .Select(line => line.Split("  ", 2))
            .ToDictionary(parts => parts[1], parts => parts[0]);
}
