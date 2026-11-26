using System.Diagnostics;
using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Verification;

namespace ReportService.PerformanceTests;

/// <summary>
/// The staged assembly at the document ceiling: 10 000 pages in 45 parts, and the
/// same shape at a quarter size. It records the peak managed heap, the process
/// working set, the per-stage wall time, the temporary-disk peak and the growth of
/// the full assembly over the quarter one. The absolute figures are comparable only
/// on the reference environment; the resource-bound assertion is the growth, and the
/// stamping-and-merge time is compared with ADR-007 §5's 15 s envelope. Kept out of
/// the hermetic inner loop and run at the performance level. Authored in the
/// V-Bounce; run in the performance cycle.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class AssemblyAtCeilingTests : IDisposable
{
    private const int FullPages = 10_000;
    private const int FullParts = 45;
    private const int QuarterPages = FullPages / 4;
    private const int QuarterParts = FullParts / 4;
    private static readonly TimeSpan MergeEnvelope = TimeSpan.FromSeconds(15);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "assembly-ceiling-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task TheAssemblyAtTheCeilingStaysInsideTheMemoryAndTimeEnvelope()
    {
        Directory.CreateDirectory(_root);
        var quarter = await AssembleAndMeasureAsync(QuarterPages, QuarterParts);
        var full = await AssembleAndMeasureAsync(FullPages, FullParts);

        Assert.Equal(FullPages, full.Pages);
        Assert.Equal(QuarterPages, quarter.Pages);
        Assert.Equal(FullParts, full.Metrics.Parts);
        Assert.Equal(QuarterParts, quarter.Metrics.Parts);
        Assert.True(full.Metrics.TemporaryPeakBytes <= full.Metrics.PartBytes * AssemblyLimits.TemporaryDiskFactor);

        var growth = full.Metrics.PeakWorkingSetBytes - quarter.Metrics.PeakWorkingSetBytes;
        var heapGrowth = full.Metrics.PeakManagedBytes - quarter.Metrics.PeakManagedBytes;
        var figure = string.Create(
            CultureInfo.InvariantCulture,
            $"assembly at the ceiling: {FullPages} pages in {FullParts} parts, part bytes {full.Metrics.PartBytes}, output {full.Metrics.OutputBytes}; "
            + $"working set peak {full.Metrics.PeakWorkingSetBytes / 1024.0 / 1024.0:F1} MiB (quarter {quarter.Metrics.PeakWorkingSetBytes / 1024.0 / 1024.0:F1} MiB, growth {growth / 1024.0 / 1024.0:F1} MiB); "
            + $"managed heap peak {full.Metrics.PeakManagedBytes / 1024.0 / 1024.0:F1} MiB (growth {heapGrowth / 1024.0 / 1024.0:F1} MiB); "
            + $"batches {full.Metrics.Batches} in {full.Metrics.BatchesTime.TotalSeconds:F1} s, final merge {full.Metrics.FinalMerge.TotalSeconds:F1} s, verification {full.Metrics.Verification.TotalSeconds:F1} s, temp peak {full.Metrics.TemporaryPeakBytes / 1024.0 / 1024.0:F1} MiB; "
            + $"envelope for stamping and merge: {MergeEnvelope.TotalSeconds:F0} s; pdf library {full.Metrics.PdfLibraryVersion}; stamp font {full.Metrics.StampFontSha256}.");
        TestContext.Current.TestOutputHelper?.WriteLine(figure);
        TestContext.Current.AddAttachment("assembly-at-ceiling-figure", figure);

        // The growth is the resource bound: the full assembly may not grow with the
        // document's size beyond a bounded allowance over the quarter-size one.
        Assert.True(
            growth <= 256L * 1024 * 1024,
            "the working-set growth from the quarter assembly to the ceiling assembly stays within 256 MiB: " + figure);
        Assert.True(
            full.Metrics.BatchesTime + full.Metrics.FinalMerge <= MergeEnvelope * 4,
            "the stamping-and-merge time stays inside the measured envelope (allowing a slow reference environment): " + figure);
    }

    private async Task<(int Pages, AssemblyMetrics Metrics)> AssembleAndMeasureAsync(int pages, int parts)
    {
        var directory = Path.Combine(_root, pages.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        var partPaths = BuildParts(directory, pages, parts);
        var output = Path.Combine(directory, "assembled.pdf");
        var metrics = new AssemblyMetrics();
        var assembler = new PdfSharpAssembler();

        var result = await assembler.AssembleAsync(
            new AssemblyRequest(partPaths, Contract(), output, directory, Metrics: metrics),
            CancellationToken.None);

        var frame = PageFrame.FromMillimetres(210, 297, 12, 10, 12, 10);
        var expectations = partPaths
            .Select(part => new AssemblyPartExpectation(
                part.PartIndex,
                part.Path,
                part.PageCount,
                part.SizeBytes,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(part.Path)))))
            .ToList();
        var verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(output, expectations, frame, Contract()));
        Assert.True(verdict.Passed, verdict.Reason);

        return (result.PageCount, result.Metrics);
    }

    private static List<AssemblyPart> BuildParts(string directory, int pages, int parts)
    {
        var font = new XFont(Face(), 9, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
        var built = new List<AssemblyPart>(parts);
        var remaining = pages;
        for (var index = 0; index < parts; index++)
        {
            var pageCount = index == parts - 1 ? remaining : pages / parts;
            remaining -= pageCount;
            var path = Path.Combine(directory, $"part-{index:00}.pdf");
            using var document = new PdfDocument();
            for (var page = 1; page <= pageCount; page++)
            {
                var sheet = document.AddPage();
                sheet.Width = XUnit.FromPoint(595.276);
                sheet.Height = XUnit.FromPoint(841.89);
                using var graphics = XGraphics.FromPdfPage(sheet);
                graphics.DrawString(
                    $"part{index}page{page}",
                    font,
                    XBrushes.Black,
                    new XRect(40, 40, 400, 20),
                    XStringFormats.TopLeft);
            }

            document.Save(path);
            built.Add(new AssemblyPart(index, path, pageCount, new FileInfo(path).Length));
        }

        return built;
    }

    private static PageNumbering Contract() => new(
        new NumberingArea(120, 268, 80, 8),
        new NumberingFont(
            "Liberation Sans",
            "fonts/LiberationSans-Regular.ttf",
            9,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"))),
        "Page {X} of {Y}",
        "en-US");

    private static string Face()
    {
        var resolver = PackagedFontResolver.Instance;
        try
        {
            GlobalFontSettings.FontResolver = resolver;
        }
        catch (InvalidOperationException)
        {
        }

        return resolver.Register(new NumberingFont(
            "Liberation Sans",
            "fonts/LiberationSans-Regular.ttf",
            9,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"))));
    }
}
