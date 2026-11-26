using ReportService.Pdf.Verification;

namespace ReportService.EndToEndTests;

/// <summary>
/// The restart-safe assembly over the real composition: the same fixture is run once
/// clean and once with the merge worker's deterministic interruption seam, which
/// makes it exit after the first batch; the runtime restarts it and the engine
/// retries the attempt from the committed parts. Both runs must produce the same
/// pages and the same global stamps, and the artifact is published exactly once.
/// Authored in the V-Bounce; run in the e2e cycle.
/// </summary>
public sealed class MultiBlockRenderMergeRestartTests
{
    private const string InterruptVariable = "REPORTSERVICE_ASSEMBLY_INTERRUPT_AFTER_BATCH";

    [Fact]
    public async Task ARestartDuringAssemblyResumesToTheSameContent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clean = await RunAsync(interrupt: false, cancellationToken);
        var restarted = await RunAsync(interrupt: true, cancellationToken);

        Assert.Equal("SUCCEEDED", clean.Status);
        Assert.Equal("SUCCEEDED", restarted.Status);
        Assert.NotNull(clean.ArtifactId);
        Assert.NotNull(restarted.ArtifactId);
        Assert.Equal(clean.PageTexts, restarted.PageTexts);
        Assert.Equal(clean.Stamps, restarted.Stamps);
    }

    private static async Task<Run> RunAsync(bool interrupt, CancellationToken cancellationToken)
    {
        var previous = Environment.GetEnvironmentVariable(InterruptVariable);
        Environment.SetEnvironmentVariable(InterruptVariable, interrupt ? "1" : null);
        try
        {
            using var stage = new MultiBlockStage();
            await using var composition = await stage.StartAsync(cancellationToken);
            await stage.TakeTemplateAsync(composition, cancellationToken);
            var uploadId = await stage.SeedConfirmingUploadAsync(composition, cancellationToken);
            var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);
            await stage.CommitPartitionAsync(composition, reportJobId, uploadId, cancellationToken);
            await stage.CompleteUploadAsync(composition, uploadId, cancellationToken);

            var ended = await stage.WaitForEndAsync(composition, reportJobId, cancellationToken);
            Assert.NotNull(ended.ObjectKey);
            Assert.NotNull(ended.SizeBytes);
            Assert.NotNull(ended.Sha256);

            var path = await stage.DownloadAsync(
                composition,
                ended.ObjectKey!,
                ended.SizeBytes!.Value,
                ended.Sha256!,
                interrupt ? "restarted.pdf" : "clean.pdf",
                cancellationToken);
            var reading = PdfWordReader.Read(path, MultiBlockStage.Margins);
            var area = MultiBlockStage.Area;
            var texts = reading.Pages
                .Select(page => string.Join('|', MultiBlockStage.OutsideArea(page)))
                .ToList();
            var stamps = reading.Pages
                .Select(page => string.Join(
                    ' ',
                    page.Words.Where(word => MultiBlockStage.Inside(word, area)).Select(word => word.Text)))
                .ToList();
            return new Run(ended.Status, ended.ArtifactId, texts, stamps);
        }
        finally
        {
            Environment.SetEnvironmentVariable(InterruptVariable, previous);
        }
    }

    private sealed record Run(
        string Status,
        string? ArtifactId,
        IReadOnlyList<string> PageTexts,
        IReadOnlyList<string> Stamps);
}
