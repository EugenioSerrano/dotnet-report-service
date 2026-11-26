using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The page box at the assembly input. A real Chromium A4 part commits
/// <c>[0 0 594.95996 841.91998]</c> (ADR-007 §1.3) while the declared frame is
/// 595.27559 x 841.88976: a 0.31563 pt width delta. The render step's
/// <see cref="PageGeometryCheck"/> already accepted that page with its one-point
/// window, and the assembly input now compares the same fact with the same window.
/// A box beyond one point is still refused, so the known-bad control keeps its
/// weight.
/// </summary>
public sealed class PageBoxToleranceTests
{
    [Fact]
    public async Task APartPrintedAtTheRealChromiumA4Passes()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(
                    directory,
                    0,
                    1,
                    widthPt: AssemblyFixture.ChromiumA4WidthPt,
                    heightPt: AssemblyFixture.ChromiumA4HeightPt),
            };

            // What the fixture printed is the box the covering run committed, and it is
            // outside the old fifth-of-a-point window on purpose.
            var box = PdfDocumentReader.Read(parts[0].Path, AssemblyFixture.Frame.Margins).Pages[0].Box;
            Assert.InRange(
                Math.Abs(box.Width - AssemblyFixture.Frame.WidthPt),
                0.05d,
                PageGeometryCheck.TolerancePt);
            Assert.InRange(
                Math.Abs(box.Height - AssemblyFixture.Frame.HeightPt),
                0d,
                PageGeometryCheck.TolerancePt);

            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            var verdict = Verify(output, parts);

            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task APageBoxAtOnePointFromTheFrameIsAccepted()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            // PDFsharp writes the box to two decimals; these are the committed boxes
            // closest to one point from the declared frame on each side, from below.
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 1, widthPt: 596.27d, heightPt: 840.89d),
            };
            var box = PdfDocumentReader.Read(parts[0].Path, AssemblyFixture.Frame.Margins).Pages[0].Box;
            Assert.InRange(
                Math.Abs(box.Width - AssemblyFixture.Frame.WidthPt),
                0.99d,
                PageGeometryCheck.TolerancePt);
            Assert.InRange(
                Math.Abs(box.Height - AssemblyFixture.Frame.HeightPt),
                0.99d,
                PageGeometryCheck.TolerancePt);

            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            var verdict = Verify(output, parts);

            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task APageBoxBeyondOnePointIsRefusedAtTheInput()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            // 596.28 is 1.00441 pt from the declared width: the first committed box
            // outside the window, and the genuine alteration ADR-007 §3.6 item 3 refuses.
            var parts = new[]
            {
                AssemblyFixture.CreatePart(
                    directory,
                    0,
                    1,
                    widthPt: 596.28d,
                    heightPt: AssemblyFixture.ChromiumA4HeightPt),
            };
            var output = Path.Combine(directory, "assembled.pdf");
            await AssemblyFixture.AssembleAsync(parts, directory, output, batchSize: 1);

            var verdict = Verify(output, parts);

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PartPageBox, verdict.Reason);
            Assert.Equal(0, verdict.PartIndex);
            Assert.Equal(1, verdict.PageNumber);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AssemblyVerdict Verify(string output, IReadOnlyList<AssemblyPart> parts) =>
        AssemblyVerifier.Verify(new AssemblyVerificationRequest(
            output,
            AssemblyFixture.Expectations(parts),
            AssemblyFixture.Frame,
            AssemblyFixture.Contract()));
}
