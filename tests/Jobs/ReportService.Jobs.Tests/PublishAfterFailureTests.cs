using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.Jobs.Tests;

/// <summary>
/// What the store allows after an output has failed: the commit transaction refuses
/// a publication and a part commit, a second commit of the same block inserts
/// nothing, and a failure of a terminal output changes nothing. The cases run
/// against the in-memory store, which is held to the same contract as the store of
/// the real database.
/// </summary>
public sealed class PublishAfterFailureTests
{
    private const string Consumer = "dev-consumer";

    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    private static readonly DateTimeOffset Confirmed = Now.AddMinutes(-1);

    private readonly InMemoryUploadStore _uploads = new();

    private readonly InMemoryJobStore _jobs;

    /// <summary>Creates the store pair every case runs against.</summary>
    public PublishAfterFailureTests() => _jobs = new InMemoryJobStore(_uploads);

    [Fact]
    public async Task PublicationAfterAFailedOutputIsRefusedAndInsertsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAndStartedAsync(cancellationToken);

        Assert.Equal(
            OutputFailureOutcome.Applied,
            await _jobs.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", reason: null, Now, cancellationToken));

        var refused = await _jobs.PublishAsync(ArtifactOf(acceptance.ReportJobId, acceptance.Outputs[0].OutputId, "artifact-1"), cancellationToken);

        Assert.Equal(PublishOutcome.GuardRefused, refused.Outcome);
        Assert.Null(await _jobs.FindArtifactAsync(acceptance.ReportJobId, "artifact-1", Consumer, cancellationToken));
        var state = await _jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(OutputStatus.Failed, Assert.Single(state!.Outputs).Status);
        Assert.Equal("PAGINATION_FAILED", state.Outputs[0].ErrorCode);
    }

    [Fact]
    public async Task APartCommitForAFailedOutputIsRefusedAndInsertsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAndStartedAsync(cancellationToken);

        Assert.Equal(
            OutputFailureOutcome.Applied,
            await _jobs.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", reason: null, Now, cancellationToken));

        var refused = await _jobs.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId), cancellationToken);

        Assert.Equal(PartCommitOutcome.GuardRefused, refused.Outcome);
        Assert.Null(await _jobs.FindPartAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken));
    }

    [Fact]
    public async Task ASecondCommitOfTheSameBlockInsertsNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAndStartedAsync(cancellationToken);

        var committed = await _jobs.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId), cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);

        var again = await _jobs.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId) with { PageCount = 9 }, cancellationToken);
        Assert.Equal(PartCommitOutcome.AlreadyCommitted, again.Outcome);
        Assert.Equal(3, again.Existing!.PageCount);
        var stored = await _jobs.FindPartAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken);
        Assert.Equal(3, stored!.PageCount);
    }

    [Fact]
    public async Task AFailureOfATerminalOutputChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAndStartedAsync(cancellationToken);

        Assert.Equal(
            OutputFailureOutcome.Applied,
            await _jobs.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", reason: null, Now, cancellationToken));
        Assert.Equal(
            OutputFailureOutcome.AlreadyTerminal,
            await _jobs.FailOutputAsync(acceptance.Outputs[0].OutputId, "EXECUTION_FAILED", reason: null, Now.AddMinutes(1), cancellationToken));

        var state = await _jobs.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("PAGINATION_FAILED", Assert.Single(state!.Outputs).ErrorCode);
    }

    private async Task<JobAcceptance> AcceptedAndStartedAsync(CancellationToken cancellationToken)
    {
        var uploadId = "upload-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture);
        var upload = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            Now.AddHours(-1),
            Now.AddHours(1));
        await _uploads.CreateAsync(upload, cancellationToken);
        Assert.True(await _uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await _uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now.AddMinutes(-20), cancellationToken));
        Assert.True(await _uploads.ConfirmAsync(
            uploadId, Consumer, Hash, Confirmed, Now.AddMinutes(-20), Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await _uploads.CommitReadyAsync(Snapshot(uploadId), Now.AddMinutes(-10), cancellationToken));

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await _jobs.AcceptAsync(acceptance, Now, cancellationToken));
        Assert.Equal(TransitionOutcome.Applied, await _jobs.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken));
        return acceptance;
    }

    private static SnapshotRecord Snapshot(string uploadId) => new(
        uploadId,
        Consumer,
        ObjectKey.FromSegments("snapshots", uploadId, "a"),
        UploadContentEncoding.None,
        DataProfile.HeaderItemsV1,
        Hash,
        900,
        Hash,
        900,
        Now.AddMinutes(-10));

    private static PartCommit Commit(string outputId) => new(
        outputId,
        0,
        $"outputs/job/{outputId}/attempt",
        Hash,
        1_024,
        3,
        Hash,
        Now);

    private static ArtifactRecord ArtifactOf(string reportJobId, string outputId, string artifactId) => new(
        artifactId,
        reportJobId,
        outputId,
        OutputFormat.Pdf,
        "application/pdf",
        $"outputs/{reportJobId}/{outputId}/attempt",
        Hash,
        2_048,
        3,
        Now,
        Now.AddDays(7));
}
