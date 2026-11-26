using System.Security.Cryptography;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;

namespace ReportService.Pdf.Assembly.Tests;

/// <summary>
/// The assembly's edge cases: a part that does not hold its committed pages, a part
/// that is not a document, a page box that is not the template's, the batch callback
/// and the batch-size clamp, and the packaged-font resolver's refusals. These are the
/// failures that keep a wrong document from being published.
/// </summary>
public sealed class AssemblyEdgeTests
{
    [Fact]
    public async Task APartThatDoesNotHoldItsCommittedPagesIsRefused()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var part = AssemblyFixture.CreatePart(directory, 0, 2);
            var wrong = part with { PageCount = 3 };

            var refused = await Assert.ThrowsAsync<AssemblyInputException>(() =>
                AssemblyFixture.AssembleAsync([wrong], directory, Path.Combine(directory, "out.pdf")));

            Assert.Contains("page count", refused.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task APartThatIsNotADocumentIsRefused()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var path = Path.Combine(directory, "broken.pdf");
            await File.WriteAllTextAsync(path, "not a document", TestContext.Current.CancellationToken);
            var part = new AssemblyPart(0, path, 1, new FileInfo(path).Length);

            var refused = await Assert.ThrowsAsync<AssemblyInputException>(() =>
                AssemblyFixture.AssembleAsync([part], directory, Path.Combine(directory, "out.pdf")));

            Assert.Contains("document", refused.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheBatchCallbackRunsOnceForEveryBatch()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 1),
                AssemblyFixture.CreatePart(directory, 1, 1),
                AssemblyFixture.CreatePart(directory, 2, 1),
            };
            var batches = new List<int>();
            var request = new AssemblyRequest(
                parts,
                AssemblyFixture.Contract(),
                Path.Combine(directory, "out.pdf"),
                directory,
                BatchSize: 2,
                OnBatch: batch =>
                {
                    batches.Add(batch);
                    return ValueTask.CompletedTask;
                });

            await new PdfSharpAssembler().AssembleAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal([1, 2], batches);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ABatchSizeBelowOneIsClampedToOne()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var parts = new[]
            {
                AssemblyFixture.CreatePart(directory, 0, 1),
                AssemblyFixture.CreatePart(directory, 1, 1),
            };
            var metrics = new AssemblyMetrics();

            var result = await new PdfSharpAssembler().AssembleAsync(
                new AssemblyRequest(
                    parts,
                    AssemblyFixture.Contract(),
                    Path.Combine(directory, "out.pdf"),
                    directory,
                    BatchSize: 0,
                    Metrics: metrics),
                TestContext.Current.CancellationToken);

            Assert.Equal(2, metrics.Batches);
            Assert.Equal(2, result.PageCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnreadablePartFailsTheVerificationAsUnreadable()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var part = AssemblyFixture.CreatePart(directory, 0, 1);
            var broken = Path.Combine(directory, "broken.pdf");
            await File.WriteAllTextAsync(broken, "not a document", TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(broken, TestContext.Current.CancellationToken);
            var expectation = new AssemblyPartExpectation(
                0,
                broken,
                1,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));

            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                part.Path,
                [expectation],
                AssemblyFixture.Frame,
                AssemblyFixture.Contract()));

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.Unreadable, verdict.Reason);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task APartWithANonTemplatePageBoxFailsTheVerification()
    {
        var directory = AssemblyFixture.NewDirectory();
        try
        {
            var source = AssemblyFixture.CreatePart(directory, 0, 1);
            var other = Path.Combine(directory, "letter.pdf");
            using (var document = new PdfDocument())
            {
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(612);
                page.Height = XUnit.FromPoint(792);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString(
                    "other",
                    new XFont(AssemblyFixture.Face(), 10, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode)),
                    XBrushes.Black,
                    new XRect(20, 20, 100, 20),
                    XStringFormats.TopLeft);
                document.Save(other);
            }

            var bytes = await File.ReadAllBytesAsync(other, TestContext.Current.CancellationToken);
            var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                other,
                [new AssemblyPartExpectation(0, other, 1, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))],
                AssemblyFixture.Frame,
                AssemblyFixture.Contract()));

            Assert.False(verdict.Passed);
            Assert.Equal(AssemblyFailureReasons.PartPageBox, verdict.Reason);
            _ = source;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheFontResolverRefusesAFaceItDoesNotHold()
    {
        var resolver = PackagedFontResolver.Instance;

        Assert.Null(resolver.ResolveTypeface("a-face-nobody-registered", isBold: false, isItalic: false));
        Assert.Null(resolver.GetFont("a-face-nobody-registered"));
    }

    [Fact]
    public void TheFontResolverServesTheRegisteredBytes()
    {
        var resolver = PackagedFontResolver.Instance;
        var font = AssemblyFixture.Font();
        var face = resolver.Register(font);

        Assert.NotNull(resolver.ResolveTypeface(face, isBold: false, isItalic: false));
        Assert.Equal(font.Bytes, resolver.GetFont(face));
    }

    [Fact]
    public async Task TheMetricsRecordEveryStage()
    {
        var metrics = new AssemblyMetrics();
        var numbering = AssemblyFixture.Contract();
        metrics.Begin([new AssemblyPart(0, "a.pdf", 1, 10)], numbering);

        await metrics.MeasureAsync(AssemblyStage.InputsCheck, () => Task.CompletedTask);
        await metrics.MeasureAsync(AssemblyStage.Batches, () => Task.CompletedTask);
        await metrics.MeasureAsync(AssemblyStage.FinalMerge, () => Task.CompletedTask);
        await metrics.MeasureAsync(AssemblyStage.Verification, () => Task.CompletedTask);
        metrics.RecordBatch(100);
        metrics.RecordTemporary(200);
        metrics.RecordPages(10);
        metrics.RecordOutput(300);

        Assert.Equal(1, metrics.Parts);
        Assert.Equal(10, metrics.Pages);
        Assert.Equal(1, metrics.Batches);
        Assert.Equal(200, metrics.TemporaryPeakBytes);
        Assert.Equal(300, metrics.OutputBytes);
        Assert.Equal(numbering.Font.Sha256, metrics.StampFontSha256);
        Assert.NotEqual("unknown", metrics.PdfLibraryVersion);
    }
}
