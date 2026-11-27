using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ReportService.Pdf.Assembly;
using ReportService.Pdf.Assembly.PageCodes;
using ReportService.Pdf.Verification;
using ReportService.Testing;

namespace ReportService.PerformanceTests;

/// <summary>
/// The capacity proving ground: one document's assembly, in process, at 10 000 pages and
/// at 50 000 pages, every page carrying its number and a per-page code. The measurement
/// asserts only what the architecture fixes - every page verified, the temporary disk
/// inside its quota of three times the parts' bytes plus one maximum stored code per page
/// - and records the rest as figures: peak working set and managed heap, the time of every
/// stage, the temporary-disk peak, the output's size and the code's bytes and time. The
/// figures are taken on synthetic one-line pages, so they are a lower bound for a real
/// document of the same page count. The run assembles one document; it says nothing about
/// a job of that size completing through the workflow. Kept out of the hermetic inner
/// loop and run at the performance level, alone on the reference machine.
/// </summary>
[Collection(nameof(MemoryMeasured))]
public sealed class DocumentCapacityProvingGroundTests : IDisposable
{
    /// <summary>The reference point: the page count of the last published measurement.</summary>
    private const int ReferencePages = 10_000;

    /// <summary>The proving ground's page count.</summary>
    private const int ProvingGroundPages = 50_000;

    /// <summary>Pages per part, the shape the 10 000-page measurement has always used (45 parts).</summary>
    private const int PagesPerPart = 223;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "capacity-proving-ground-" + Guid.NewGuid().ToString("N"));

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
    public async Task TheProvingGroundIsMeasuredAtTenAndFiftyThousandPagesWithCodes()
    {
        Directory.CreateDirectory(_root);

        var reference = await MeasureAsync(ReferencePages);
        Write("capacity-reference-10k.json", reference);

        var provingGround = await MeasureAsync(ProvingGroundPages);
        Write("capacity-proving-ground-50k.json", provingGround);

        var growthPerThousandPages =
            (provingGround.PeakWorkingSetBytes - reference.PeakWorkingSetBytes)
            / ((ProvingGroundPages - ReferencePages) / 1_000.0);
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"capacity proving ground (synthetic one-line pages, lower bound): "
            + $"10k peak working set {Mib(reference.PeakWorkingSetBytes):F1} MiB, 50k {Mib(provingGround.PeakWorkingSetBytes):F1} MiB, "
            + $"growth {Mib((long)growthPerThousandPages):F2} MiB per 1 000 pages; "
            + $"50k stamping+merge {provingGround.BatchesSeconds + provingGround.FinalMergeSeconds:F1} s, verification {provingGround.VerificationSeconds:F1} s, "
            + $"code generation {provingGround.CodeGenerationSeconds:F1} s, output {Mib(provingGround.OutputBytes):F1} MiB, "
            + $"temporary peak {Mib(provingGround.TemporaryPeakBytes):F1} MiB of a {Mib(provingGround.TemporaryQuotaBytes):F1} MiB quota.");
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        TestContext.Current.AddAttachment("capacity-proving-ground-summary", summary);
        WriteText("capacity-proving-ground-summary.txt", summary);
    }

    private async Task<CapacityFigures> MeasureAsync(int pages)
    {
        var directory = Path.Combine(_root, pages.ToString(CultureInfo.InvariantCulture));
        var staging = Path.Combine(directory, "assembly");
        Directory.CreateDirectory(staging);
        var parts = SyntheticAssemblyParts.Build(directory, pages, (pages + PagesPerPart - 1) / PagesPerPart);
        var partBytes = parts.Sum(part => part.SizeBytes);
        var code = SyntheticAssemblyParts.Code();
        var output = Path.Combine(staging, "assembled.pdf");
        var metrics = new AssemblyMetrics();
        var quota = (partBytes * AssemblyLimits.TemporaryDiskFactor)
            + ((long)pages * ReportService.Pdf.Verification.PageCodes.PageCodeContentValidator.MaximumStoredBytes);

        var watch = Stopwatch.StartNew();
        var result = await new PdfSharpAssembler().AssembleAsync(
            new AssemblyRequest(
                parts,
                SyntheticAssemblyParts.Contract(),
                output,
                staging,
                Metrics: metrics,
                PageCode: code,
                MaximumPages: pages,
                MaximumPartBytes: partBytes),
            CancellationToken.None);

        var verdict = AssemblyVerdict.Good;
        await metrics.MeasureAsync(AssemblyStage.Verification, () =>
        {
            verdict = AssemblyVerifier.Verify(new AssemblyVerificationRequest(
                output,
                SyntheticAssemblyParts.Expectations(parts),
                SyntheticAssemblyParts.Frame,
                SyntheticAssemblyParts.Contract(),
                code));
            return Task.CompletedTask;
        });
        watch.Stop();

        // What the architecture fixes: every page verified, and the temporary disk inside
        // its quota. Everything else is a figure, recorded and published, never a threshold.
        Assert.True(verdict.Passed, verdict.Reason);
        Assert.Equal(pages, result.PageCount);
        Assert.Equal(pages, metrics.CodePages);
        Assert.Equal(0, metrics.CodeFailures);
        Assert.True(
            metrics.TemporaryPeakBytes <= quota,
            string.Create(CultureInfo.InvariantCulture, $"temporary peak {metrics.TemporaryPeakBytes} bytes within the quota of {quota} bytes"));

        return new CapacityFigures(
            SyntheticAssemblyParts.BuilderName,
            "lower bound for the real row mix",
            pages,
            parts.Count,
            partBytes,
            metrics.Batches,
            metrics.PeakWorkingSetBytes,
            metrics.PeakManagedBytes,
            metrics.InputsCheck.TotalSeconds,
            metrics.BatchesTime.TotalSeconds,
            metrics.FinalMerge.TotalSeconds,
            metrics.Verification.TotalSeconds,
            metrics.CodeGeneration.TotalSeconds,
            watch.Elapsed.TotalSeconds,
            metrics.TemporaryPeakBytes,
            quota,
            metrics.OutputBytes,
            metrics.CodePages,
            metrics.CodeBytes,
            metrics.PdfLibraryVersion,
            metrics.CodeGenerator?.Name ?? string.Empty,
            metrics.CodeGenerator?.AssemblyVersion ?? string.Empty,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Environment.ProcessorCount);
    }

    private static double Mib(long bytes) => bytes / 1024.0 / 1024.0;

    private static void Write(string fileName, CapacityFigures figures)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(figures, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Current.TestOutputHelper?.WriteLine(Encoding.UTF8.GetString(json));
        if (RunEnvironment.RunDirectory is not null)
        {
            RunEnvironment.WriteEvidence(fileName, json);
        }
    }

    private static void WriteText(string fileName, string text)
    {
        if (RunEnvironment.RunDirectory is not null)
        {
            RunEnvironment.WriteEvidence(fileName, Encoding.UTF8.GetBytes(text + Environment.NewLine));
        }
    }

    /// <summary>One measured assembly, as the record publishes it.</summary>
    private sealed record CapacityFigures(
        string PartBuilder,
        string Validity,
        int Pages,
        int Parts,
        long PartBytes,
        int Batches,
        long PeakWorkingSetBytes,
        long PeakManagedBytes,
        double InputsCheckSeconds,
        double BatchesSeconds,
        double FinalMergeSeconds,
        double VerificationSeconds,
        double CodeGenerationSeconds,
        double WallSeconds,
        long TemporaryPeakBytes,
        long TemporaryQuotaBytes,
        long OutputBytes,
        int CodePages,
        long CodeBytes,
        string PdfLibraryVersion,
        string CodeGenerator,
        string CodeGeneratorVersion,
        long HostAvailableMemoryBytes,
        int ProcessorCount);
}
