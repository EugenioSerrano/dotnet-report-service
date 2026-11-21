extern alias orchestration;

using ReportService.Testing.VerificationLoss;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A print that misses a record or part of its text, driven as a real job through the product's
/// own API, orchestration worker and published render worker image: PostgreSQL 18.6 with the
/// product's migrations, Temporal 1.9.1, one storage root and the verification-loss corpus
/// recorded as versions already taken. Each refused document is asserted as one state: the PDF
/// output and the job failed with the same code, no artifact, no part commit, no object under
/// the output's prefix, one render attempt, and the one reason the delivered gate reported -
/// before printing for a source check, after printing for the printed-text check. The honest
/// document of the corpus is the control: it is printed, published once, and its artifact, its
/// part commit and the stored object agree on size and SHA-256.
/// </summary>
[Trait("Requires", "printing")]
public sealed class VerificationRefusalTests : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private const string RefusalCode = "PAGINATION_FAILED";

    private readonly PostgresFixture _fixture;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public VerificationRefusalTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Each loss document, the one reason the gate reports and where the refusal happened.</summary>
    public static TheoryData<string, string, bool> Refusals => new()
    {
        // A row that prints no text: the source check stops the block before anything is printed.
        { VerificationLossCorpus.RowLoss, "row-without-text", true },
        // A value holding a character the browser does not paint: only the printed text shows it.
        { VerificationLossCorpus.TextLoss, "text-dropped", false },
        // Both data defects in one document: the source checks stop at the empty row first.
        { VerificationLossCorpus.ManyLoss, "row-without-text", true },
    };

    /// <summary>
    /// A document whose print misses a record or part of its text ends with a failed output and a
    /// failed job carrying the refusal's code, nothing to download and one render attempt.
    /// </summary>
    /// <param name="document">The corpus document the job prints.</param>
    /// <param name="reason">The reason the delivered gate reports for it.</param>
    /// <param name="beforePrinting">Whether the refusal happens before anything is printed.</param>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task APrintThatLosesARecordOrPartOfItsTextIsRefusedAndNothingIsPublished(
        string document,
        string reason,
        bool beforePrinting)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await RefusalCompositionAsync(cancellationToken);

        var (reportJobId, job) = await SubmitAsync(rig, document, cancellationToken);

        await AssertRefusedAsync(rig, reportJobId, job, reason, beforePrinting, cancellationToken);
    }

    /// <summary>
    /// The same document printed again is refused the same way: the same code, the same reason
    /// and the same one-attempt shape, because a layout is deterministic under fixed inputs.
    /// </summary>
    [Fact]
    public async Task TheSameDocumentIsRefusedTheSameWayOnASecondJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await RefusalCompositionAsync(cancellationToken);

        var (firstJobId, first) = await SubmitAsync(rig, VerificationLossCorpus.ManyLoss, cancellationToken);
        await AssertRefusedAsync(rig, firstJobId, first, "row-without-text", beforePrinting: true, cancellationToken);

        var (secondJobId, second) = await SubmitAsync(rig, VerificationLossCorpus.ManyLoss, cancellationToken);
        await AssertRefusedAsync(rig, secondJobId, second, "row-without-text", beforePrinting: true, cancellationToken);

        Assert.Equal(first.OutputErrorCode, second.OutputErrorCode);
        Assert.Equal(
            await ReasonOf(rig, firstJobId, cancellationToken),
            await ReasonOf(rig, secondJobId, cancellationToken));
    }

    /// <summary>
    /// The honest document of the corpus is not refused: it is printed, published once, and the
    /// artifact, the part commit and the stored object agree on size and SHA-256 - the refusal is
    /// a property of wrongness, never of PDFs in general.
    /// </summary>
    [Fact]
    public async Task TheHonestDocumentIsPrintedAndPublishedOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await RefusalCompositionAsync(cancellationToken);

        var (reportJobId, job) = await SubmitAsync(rig, VerificationLossCorpus.Honest, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);
        Assert.Equal("SUCCEEDED", job.OutputStatus);
        Assert.Null(job.OutputErrorCode);
        Assert.NotNull(job.ArtifactId);
        Assert.Equal(1L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));

        // The binding: the artifact names the part commit's object, and the three records agree.
        Assert.Equal(job.PartObjectKey, job.ObjectKey);
        Assert.Equal(job.PartSha256, job.Sha256);
        Assert.Equal(job.PartSizeBytes, job.SizeBytes);

        var bytes = await rig.ReadArtifactAsync(job, cancellationToken);
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        var history = await rig.OutputHistoryAsync(reportJobId, cancellationToken);
        Assert.Single(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        Assert.Single(Activities(history, PdfOutputActivities.PublishOutputName));
    }

    // One running composition: the published render worker image, the API and the orchestration
    // worker from the product's own registrations, and the corpus recorded as taken versions.
    private async Task<PrintingRig> RefusalCompositionAsync(CancellationToken cancellationToken)
    {
        var rig = await PrintingRig.StartAsync(
            _fixture,
            startPdfAsImage: true,
            extraConsumers: [VerificationLossCorpus.Consumer],
            cancellationToken: cancellationToken);
        try
        {
            await rig.SeedCorpusAsync(cancellationToken);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }

        return rig;
    }

    private static async Task<(string ReportJobId, JobStateRow Job)> SubmitAsync(
        PrintingRig rig,
        string document,
        CancellationToken cancellationToken)
    {
        var snapshot = VerificationLossCorpus.Snapshot(document);
        var uploadId = await rig.ReadyUploadAsync(snapshot.Bytes, cancellationToken, VerificationLossCorpus.Consumer);
        var reportJobId = await rig.SubmitAsync(
            uploadId,
            VerificationLossCorpus.Template,
            VerificationLossCorpus.Version,
            culture: null,
            timeZone: null,
            cancellationToken,
            VerificationLossCorpus.Consumer);
        return (reportJobId, await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken));
    }

    // The refusal's whole state, asserted once: the two rows carry the same code, nothing a
    // consumer could reach was written, the render ran once on its first attempt, and the one
    // reason the gate reported is the one the corpus declares.
    private static async Task AssertRefusedAsync(
        PrintingRig rig,
        string reportJobId,
        JobStateRow job,
        string reason,
        bool beforePrinting,
        CancellationToken cancellationToken)
    {
        Assert.Equal("FAILED", job.Status);
        Assert.Equal(RefusalCode, job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal(RefusalCode, job.OutputErrorCode);

        // Nothing a consumer could reach exists: no artifact row, no part commit and no object
        // under the output's own prefix - a verification refusal writes nothing at all.
        Assert.Null(job.ArtifactId);
        var artifacts = await rig.Jobs.ListArtifactsAsync(reportJobId, VerificationLossCorpus.Consumer, cancellationToken);
        Assert.True(artifacts is null || artifacts.Artifacts.Count == 0, "a refused job publishes no artifact");
        Assert.Equal(0L, await rig.CountPartCommitsAsync(job.OutputId!, cancellationToken));
        Assert.Empty(rig.OutputObjectKeys(reportJobId, job.OutputId!));

        // The block ran once, on its first attempt: the positive form of "not retried".
        var history = await rig.OutputHistoryAsync(reportJobId, cancellationToken);
        Assert.Single(Activities(history, PdfOutputStepContracts.RenderPdfBlock));
        var attempts = StartedAttempts(history, PdfOutputStepContracts.RenderPdfBlock);
        Assert.Equal(new[] { 1 }, attempts);
        Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));

        var message = Assert.Single(Failures(history));
        Assert.Contains(RefusalCode + " reason=" + reason, message, StringComparison.Ordinal);
        Assert.Equal(beforePrinting, !message.Contains(" page=", StringComparison.Ordinal));
    }

    // The failure message and the reason a refused child execution recorded: the block's failure
    // is where the delivered gate writes its verdict, because a failed output carries no reason.
    private static async Task<string> ReasonOf(PrintingRig rig, string reportJobId, CancellationToken cancellationToken)
    {
        var history = await rig.OutputHistoryAsync(reportJobId, cancellationToken);
        return Assert.Single(Failures(history));
    }

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    // The attempt numbers of the started events of one activity, in the order they started.
    private static IReadOnlyList<int> StartedAttempts(WorkflowHistory history, string name)
    {
        var scheduled = Activities(history, name).Select(entry => entry.EventId).ToHashSet();
        return
        [
            .. history.Events
                .Where(entry => entry.EventType == EventType.ActivityTaskStarted)
                .Where(entry => scheduled.Contains(entry.ActivityTaskStartedEventAttributes.ScheduledEventId))
                .Select(entry => entry.ActivityTaskStartedEventAttributes.Attempt),
        ];
    }

    // The messages of the failed activity tasks of the execution: the refusal and whatever the
    // engine recorded beside it.
    private static IReadOnlyList<string> Failures(WorkflowHistory history) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskFailed)
            .Select(entry => entry.ActivityTaskFailedEventAttributes.Failure?.Message ?? string.Empty)
            .Where(message => message.Length > 0)];
}
