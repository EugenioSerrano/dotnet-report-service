using System.Globalization;
using System.Text;
using ReportService.Testing;
using ReportService.Testing.MultiBlock;

namespace ReportService.EndToEndTests;

/// <summary>
/// A document exactly at the configured capacity is published over the whole local
/// composition. The page count is never assumed: a first composition, configured well
/// above the fixture, prints the exact-capacity snapshot and the case reads the pages its
/// parts committed; a second composition is then configured with exactly that page count
/// and the same snapshot is printed, assembled and published, its committed pages equal to
/// the configured value. The two compositions run one after the other, never together.
/// </summary>
public sealed class AtCapacityPdfJobEndToEndTests
{
    // Each composition prints a few hundred pages; the covering run records its own
    // --timeout and this deadline is each job's own bound.
    private static readonly TimeSpan AtCapacityDeadline = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task ADocumentExactlyAtTheConfiguredPagesIsPublished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The measuring run: a capacity far above the fixture, so its page count is read.
        var measured = await PrintAsync(maximumPages: 1_000_000, "e2e-at-capacity-measure", cancellationToken);
        Assert.Equal("SUCCEEDED", measured.Ended.Status);
        Assert.True(measured.Blocks > 1, "the snapshot is printed as more than one block, so the document is assembled");

        // The case: the capacity is exactly the page count the same snapshot prints.
        var exact = await PrintAsync(measured.Pages, "e2e-at-capacity", cancellationToken);

        Assert.Equal("SUCCEEDED", exact.Ended.Status);
        Assert.Equal("SUCCEEDED", exact.Ended.OutputStatus);
        Assert.Null(exact.Ended.OutputErrorCode);
        Assert.NotNull(exact.Ended.ArtifactId);
        Assert.Equal(measured.Pages, exact.Pages);

        RunEnvironment.WriteEvidence(
            "at-capacity.evidence.txt",
            Encoding.UTF8.GetBytes(
                $"configuredPages={measured.Pages.ToString(CultureInfo.InvariantCulture)} "
                + $"committedPages={exact.Pages.ToString(CultureInfo.InvariantCulture)} "
                + $"blocks={exact.Blocks.ToString(CultureInfo.InvariantCulture)} "
                + $"status={exact.Ended.Status} artifact={exact.Ended.ArtifactId}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"at-capacity: configured={measured.Pages} committed={exact.Pages} blocks={exact.Blocks}");
    }

    private static async Task<(MultiBlockStage.EndedJob Ended, int Pages, int Blocks)> PrintAsync(
        int maximumPages,
        string profile,
        CancellationToken cancellationToken)
    {
        using var stage = new MultiBlockStage(
            accounted: true,
            templateId: "at-capacity",
            snapshotFactory: _ => MultiBlockFixtures.CapacitySnapshot(MultiBlockFixtures.ExactCapacityGroups),
            packageFactory: MultiBlockFixtures.CapacityPackage,
            compositionArguments:
            [
                "--DocumentCapacity:MaximumPages=" + maximumPages.ToString(CultureInfo.InvariantCulture),
                "--DocumentCapacity:ProfileName=" + profile,
            ]);
        await using var composition = await stage.StartAsync(cancellationToken);
        await stage.TakeTemplateAsync(composition, cancellationToken);
        var uploadId = await stage.SeedConfirmingUploadAsync(composition, cancellationToken, MultiBlockFixtures.ExactCapacityRecords);
        var reportJobId = await stage.SubmitAsync(composition, uploadId, cancellationToken);
        await stage.CompleteUploadAsync(composition, uploadId, cancellationToken);
        var ended = await stage.WaitForEndAsync(composition, reportJobId, cancellationToken, AtCapacityDeadline);
        Assert.NotNull(ended.OutputId);
        var parts = await stage.ReadPartsAsync(composition, ended.OutputId!, cancellationToken);
        return (ended, parts.Sum(part => part.PageCount), parts.Count);
    }
}
