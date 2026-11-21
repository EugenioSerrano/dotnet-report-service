using System.Text;
using ReportService.Jobs;
using ReportService.Pdf.Verification;
using ReportService.Rendering;
using ReportService.ServiceDefaults;
using ReportService.Testing;
using ReportService.Testing.VerificationLoss;
using ReportService.Workers.Pdf.Rendering;
using Temporalio.Exceptions;
using Temporalio.Testing;

namespace ReportService.Workers.Pdf.Tests.Rendering;

/// <summary>
/// The refusal's diagnostic: the failure's message, every line the step logs and the
/// tags of the measurement it publishes name the reason, the block, the page and the
/// record - and never a value of the report. The block the case renders carries the
/// corpus's planted values, so a leak is caught rather than described.
/// </summary>
public sealed class RefusalDiagnosticTests : IDisposable
{
    private readonly RenderStepHarness _harness = new();

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ARefusedBlockCarriesNoValueInItsMessageItsLogsOrItsMeasurement()
    {
        var block = Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":{\"reportTitle\":\"" + VerificationLossCorpus.PlantedHeader
            + "\",\"customer\":{\"id\":\"C-VLROW\"}},\"items\":[{\"sequence\":1,\"description\":\""
            + VerificationLossCorpus.PlantedTextLoss + "\"}]}");
        var storage = await _harness.StorageAsync(block);
        var package = await RenderStepHarness.PackageAsync(storage.Store);
        var pin = RenderStepHarness.Pin(package.Sha256);
        var jobs = new FakeJobStore
        {
            Reference = RenderStepHarness.ReferenceOf(pin),
            Partition = new PartitionRecord(
                RenderStepHarness.JobId,
                1,
                1,
                1,
                storage.Block.SizeBytes,
                storage.Index.Key,
                storage.Index.Sha256,
                storage.Index.SizeBytes,
                DateTimeOffset.UnixEpoch),
        };
        using var logs = new CapturingLogs();
        using var measurements = new RecordedMeasurements(MeterIdentity.ForHost(TelemetryDefaults.MeterName));
        var activity = _harness.Activity(
            jobs,
            new FakeVersionStore { Record = RenderStepHarness.Record(pin.PackageSha256!, package.Key, package.Size) },
            new VerificationRefusingRenderer(),
            storage.Store,
            loggers: logs.Factory);

        var failure = await Assert.ThrowsAsync<ApplicationFailureException>(
            () => new ActivityEnvironment().RunAsync(() => activity.RunAsync(new RenderPdfBlockInput(
                RenderStepHarness.JobId,
                RenderStepHarness.OutputId,
                0))));

        Assert.Equal("PAGINATION_FAILED", failure.ErrorType);
        Assert.True(failure.NonRetryable);
        Assert.Contains("text-dropped", failure.Message, StringComparison.Ordinal);
        AssertNoPlantedValue(failure.Message);

        Assert.NotEmpty(logs.Lines);
        Assert.Contains(logs.Lines, line => line.Contains("text-dropped", StringComparison.Ordinal));
        foreach (var line in logs.Lines)
        {
            AssertNoPlantedValue(line);
        }

        // The meter is process-wide: another case of this assembly may publish on it at
        // the same time, so the measurement is found by its tags, not by being the only one.
        var verifications = measurements.Of("reportservice.pdf.verification.outcomes");
        Assert.Contains(
            verifications,
            measurement => string.Equals(measurement.Tag("result"), "fail", StringComparison.Ordinal)
                && string.Equals(measurement.Tag("class"), "text-dropped", StringComparison.Ordinal));
        foreach (var measurement in verifications)
        {
            foreach (var tag in measurement.Tags)
            {
                AssertNoPlantedValue(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
    }

    private static void AssertNoPlantedValue(string text)
    {
        Assert.DoesNotContain(VerificationLossCorpus.PlantedTextLoss, text, StringComparison.Ordinal);
        Assert.DoesNotContain(VerificationLossCorpus.PlantedHeader, text, StringComparison.Ordinal);
        Assert.DoesNotContain("C-VLROW", text, StringComparison.Ordinal);
    }
}

/// <summary>The renderer as a block that failed its checks reaches the step: no file, nothing written, the reason of the first failing check.</summary>
internal sealed class VerificationRefusingRenderer : IBlockRenderer
{
    /// <inheritdoc />
    public Task<BlockRenderOutcome> RenderAsync(BlockRenderRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlockRenderOutcome(
            PdfPath: null,
            SizeBytes: 0,
            Sha256: null,
            Print: null,
            new VerificationResult(
                Passed: false,
                Reason: "text-dropped",
                BlockIndex: 0,
                RecordOrdinal: 1,
                AnchorPath: "$.description",
                Page: 1,
                UnitIndex: 1,
                Offset: 2,
                Pages: 1,
                Words: 3,
                ReadAndAlignMs: 1,
                SourceChecksMs: 1)));
}
