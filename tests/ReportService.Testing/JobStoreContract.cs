using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.Uploads;

namespace ReportService.Testing;

/// <summary>
/// The behaviour every report job store must show, written once and run against
/// both of them: the in-memory double the component tests use and the store of the
/// real database. Because it is the same class in both places, the double cannot
/// quietly drift away from the store it stands in for — a change to the contract
/// fails one of them. The uploads an acceptance is judged against are made through
/// the upload store's own steps, so each case starts from a state the service can
/// actually reach, and every instant is fixed so a boundary is decided exactly.
/// </summary>
public abstract class JobStoreContract : IAsyncLifetime
{
    /// <summary>The consumer every case accepts its jobs for.</summary>
    public const string Consumer = "dev-consumer";

    /// <summary>The instant every case decides at.</summary>
    protected static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 41, 2, 456, TimeSpan.Zero);

    /// <summary>The instant every confirmation of this contract happened at.</summary>
    protected static readonly DateTimeOffset Confirmed = Now.AddMinutes(-1);

    /// <summary>The lower-case hexadecimal hash every case of this contract uses.</summary>
    protected const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    private int _sequence;

    /// <summary>The job records under test, and the upload records they are judged against.</summary>
    protected IJobStore Store { get; private set; } = null!;

    /// <summary>The upload records an acceptance is judged against.</summary>
    protected IUploadStore Uploads { get; private set; } = null!;

    /// <summary>The states an upload can be in when a job is accepted against it.</summary>
    protected enum UploadState
    {
        Created,
        Uploading,
        Verifying,
        Ready,
        Rejected,
        Expired,
        VerifyingPastItsDeadline,
    }

    /// <summary>Creates the two stores this class's cases run against.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected abstract Task<(IJobStore Jobs, IUploadStore Uploads)> CreateStoresAsync(CancellationToken cancellationToken);

    /// <summary>Releases what <see cref="CreateStoresAsync"/> created, when the stores hold anything.</summary>
    /// <param name="jobs">The job records.</param>
    /// <param name="uploads">The upload records.</param>
    protected virtual ValueTask DisposeStoresAsync(IJobStore jobs, IUploadStore uploads) => default;

    [Fact]
    public async Task AcceptingAJobStoresItWithItsOutputAndItsStartRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);

        var result = await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var accepted = Assert.IsType<JobAcceptanceResult.Accepted>(result);
        Assert.Equal(acceptance.ReportJobId, accepted.Job.ReportJobId);
        Assert.Equal(Consumer, accepted.Job.ConsumerId);
        Assert.Equal(uploadId, accepted.Job.UploadId);
        Assert.Equal(ReportJobStatus.Queued, accepted.Job.Status);
        Assert.Equal(Now, accepted.Job.ReportClock);
        Assert.Equal(Now, accepted.Job.CreatedAt);
        Assert.Null(accepted.Job.Stage);
        Assert.Null(accepted.Job.ErrorCode);
        Assert.Null(accepted.Job.StartedAt);
        Assert.Null(accepted.Job.FinishedAt);
        Assert.False(accepted.Job.IsTerminal);

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(ReportJobStatus.Queued, state!.Job.Status);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(acceptance.OutputId, output.OutputId);
        Assert.Equal(OutputFormat.Pdf, output.Format);
        Assert.Equal(OutputStatus.Pending, output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Equal(UploadStatus.Ready, state.UploadStatus);
        Assert.Equal(Now.AddHours(1), state.UploadExpiresAt);
    }

    [Fact]
    public async Task AVerifyingUploadThatIsStillInTimeIsAccepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);

        var result = await Store.AcceptAsync(Acceptance(uploadId), Now, cancellationToken);

        Assert.IsType<JobAcceptanceResult.Accepted>(result);
        var state = await Store.FindForWorkflowAsync(((JobAcceptanceResult.Accepted)result).Job.ReportJobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(UploadStatus.Verifying, state!.UploadStatus);
        Assert.Equal(Now.AddHours(5), state.VerificationDeadline);
    }

    [Fact]
    public async Task EveryUnusableDataReferenceIsRefusedAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var state in new[]
                 {
                     UploadState.Created,
                     UploadState.Uploading,
                     UploadState.Rejected,
                     UploadState.Expired,
                     UploadState.VerifyingPastItsDeadline,
                 })
        {
            var uploadId = await SeedAsync(state, cancellationToken: cancellationToken);
            var acceptance = Acceptance(uploadId);

            var result = await Store.AcceptAsync(acceptance, Now, cancellationToken);

            Assert.True(result is JobAcceptanceResult.UploadUnusable, $"an upload in {state} must not be usable");
            Assert.Null(await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken));
        }

        var ofAnotherConsumer = await SeedAsync(UploadState.Ready, "another-consumer", cancellationToken);
        var foreign = Acceptance(ofAnotherConsumer);
        Assert.True(await Store.AcceptAsync(foreign, Now, cancellationToken) is JobAcceptanceResult.UploadUnusable);
        Assert.Null(await Store.FindForWorkflowAsync(foreign.ReportJobId, cancellationToken));

        var absent = Acceptance("upload-ffffffffffffffffffffffffffffffff");
        Assert.True(await Store.AcceptAsync(absent, Now, cancellationToken) is JobAcceptanceResult.UploadUnusable);
        Assert.Null(await Store.FindForWorkflowAsync(absent.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AVerifyingUploadIsJudgedAtTheInstantOfAcceptanceToTheMillisecond()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var deadline = Now.AddHours(5);
        var justInTime = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var atItsDeadline = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);

        Assert.IsType<JobAcceptanceResult.Accepted>(
            await Store.AcceptAsync(Acceptance(justInTime), deadline.AddMilliseconds(-1), cancellationToken));

        var late = Acceptance(atItsDeadline);
        Assert.True(await Store.AcceptAsync(late, deadline, cancellationToken) is JobAcceptanceResult.UploadUnusable);
        Assert.Null(await Store.FindForWorkflowAsync(late.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task FailingAJobEndsTheJobAndEveryOutputThatHadNotEnded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        var endedAt = Now.AddSeconds(3);
        var outcome = await Store.FailJobAsync(
            acceptance.ReportJobId, "INPUT_NOT_FOUND", "input-expired", endedAt, cancellationToken);

        Assert.Equal(JobFailureOutcome.Applied, outcome);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal(ReportJobStatus.Failed, state!.Job.Status);
        Assert.Equal("INPUT_NOT_FOUND", state.Job.ErrorCode);
        Assert.Equal("input-expired", state.Job.ErrorReason);
        Assert.Equal(endedAt, state.Job.FinishedAt);
        Assert.True(state.Job.IsTerminal);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(OutputStatus.Failed, output.Status);
        Assert.Equal("INPUT_NOT_FOUND", output.ErrorCode);
        Assert.Equal(endedAt, output.FinishedAt);
    }

    [Fact]
    public async Task AJobEndsOnceAndKeepsTheConditionItFirstEndedWith()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        Assert.Equal(
            JobFailureOutcome.Applied,
            await Store.FailJobAsync(acceptance.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now.AddSeconds(1), cancellationToken));
        Assert.Equal(
            JobFailureOutcome.AlreadyTerminal,
            await Store.FailJobAsync(acceptance.ReportJobId, "TEMPLATE_NOT_FOUND", "template-missing", Now.AddSeconds(2), cancellationToken));

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(state);
        Assert.Equal("INPUT_NOT_FOUND", state!.Job.ErrorCode);
        Assert.Equal("input-expired", state.Job.ErrorReason);
        Assert.Equal(Now.AddSeconds(1), state.Job.FinishedAt);
        var output = Assert.Single(state.Outputs);
        Assert.Equal("INPUT_NOT_FOUND", output.ErrorCode);
        Assert.Equal(Now.AddSeconds(1), output.FinishedAt);
    }

    [Fact]
    public async Task AnUnknownJobIsNotFoundAndFailingItChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var unknown = "job-ffffffffffffffffffffffffffffffff";

        Assert.Null(await Store.FindForWorkflowAsync(unknown, cancellationToken));
        Assert.Equal(
            JobFailureOutcome.AlreadyTerminal,
            await Store.FailJobAsync(unknown, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken));
        Assert.Null(await Store.FindForWorkflowAsync(unknown, cancellationToken));
    }

    [Fact]
    public async Task APartitionIsRecordedOnceAndReadBack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        var partition = Partition(acceptance.ReportJobId);

        var first = await Store.CommitPartitionAsync(partition, cancellationToken);
        var second = await Store.CommitPartitionAsync(partition with { RecordCount = 999 }, cancellationToken);

        Assert.Equal(PartitionCommitOutcome.Committed, first.Outcome);
        Assert.Equal(PartitionCommitOutcome.AlreadyCommitted, second.Outcome);
        Assert.Equal(partition.RecordCount, second.Existing.RecordCount);
        var read = await Store.FindPartitionAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(read);
        Assert.Equal(partition.IndexSha256, read!.IndexSha256);
    }

    [Fact]
    public async Task AJobTransitionStartsOnlyFromTheStatesItNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Equal(
            TransitionOutcome.NotApplicable,
            (await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Running], ReportJobStatus.Validating, Now, cancellationToken)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken)).Outcome);
        Assert.Equal(
            TransitionOutcome.AlreadyApplied,
            (await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken)).Outcome);

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Running, state!.Job.Status);
        Assert.Equal(Now, state.Job.StartedAt);
    }

    [Fact]
    public async Task AnOutputStartsOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Equal(TransitionOutcome.Applied, await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken));
        Assert.Equal(TransitionOutcome.AlreadyApplied, await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken));
    }

    [Fact]
    public async Task ABlockIsCommittedOnlyForARunningOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var refused = await Store.CommitPartAsync(Commit(acceptance.OutputId), cancellationToken);
        Assert.Equal(PartCommitOutcome.GuardRefused, refused.Outcome);
        Assert.Null(await Store.FindPartAsync(acceptance.OutputId, 0, cancellationToken));

        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var committed = await Store.CommitPartAsync(Commit(acceptance.OutputId), cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);
        Assert.Equal(Hash, committed.Existing!.Sha256);

        var again = await Store.CommitPartAsync(Commit(acceptance.OutputId) with { PageCount = 9 }, cancellationToken);
        Assert.Equal(PartCommitOutcome.AlreadyCommitted, again.Outcome);
        Assert.Equal(3, again.Existing!.PageCount);
    }

    [Fact]
    public async Task PublicationSucceedsTheOutputOnceAndClosesTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        var published = await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken);
        Assert.Equal(PublishOutcome.Published, published.Outcome);
        Assert.Equal(ArtifactId, published.ArtifactId);

        var again = await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken);
        Assert.Equal(PublishOutcome.AlreadyPublished, again.Outcome);

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(OutputStatus.Succeeded, Assert.Single(state!.Outputs).Status);

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Succeeded, closed.Status);
        Assert.Equal(
            JobFinalizationOutcome.AlreadyTerminal,
            (await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(2), cancellationToken)).Outcome);
    }

    [Fact]
    public async Task AFailedOutputFailsTheJobWithItsCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        Assert.Equal(OutputFailureOutcome.Applied, await Store.FailOutputAsync(acceptance.OutputId, "PAGINATION_FAILED", Now, cancellationToken));
        Assert.Equal(
            OutputFailureOutcome.AlreadyTerminal,
            await Store.FailOutputAsync(acceptance.OutputId, "PAGINATION_FAILED", Now, cancellationToken));

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("PAGINATION_FAILED", state!.Job.ErrorCode);
        Assert.Equal("output-failed", state.Job.ErrorReason);
    }

    [Fact]
    public async Task RecordingTheWaitOfAQueuedJobAppliesTheStageAndARepeatChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Equal(BeginInputWaitOutcome.Applied, await Store.BeginInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        var waiting = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(InputWaitStage.WaitingForInput, waiting!.Job.Stage);
        Assert.Equal(ReportJobStatus.Queued, waiting.Job.Status);

        Assert.Equal(BeginInputWaitOutcome.Applied, await Store.BeginInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        var again = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(InputWaitStage.WaitingForInput, again!.Job.Stage);
        Assert.Equal(waiting.Job, again.Job);
    }

    [Fact]
    public async Task AJobThatHasEndedNeverWaitsAndAnAbsentJobIsNotQueued()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await Store.FailJobAsync(acceptance.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken));

        Assert.Equal(BeginInputWaitOutcome.NotQueued, await Store.BeginInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        var ended = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(ended!.Job.Stage);
        Assert.Equal(ReportJobStatus.Failed, ended.Job.Status);

        Assert.Equal(
            BeginInputWaitOutcome.NotQueued,
            await Store.BeginInputWaitAsync("job-ffffffffffffffffffffffffffffffff", cancellationToken));
    }

    [Fact]
    public async Task ClearingTheWaitAppliesOnceAndARepeatIsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.BeginInputWaitAsync(acceptance.ReportJobId, cancellationToken);

        Assert.Equal(EndInputWaitOutcome.Applied, await Store.EndInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        var cleared = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(cleared!.Job.Stage);
        Assert.Equal(ReportJobStatus.Queued, cleared.Job.Status);

        Assert.Equal(EndInputWaitOutcome.NotWaiting, await Store.EndInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        Assert.Null((await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Job.Stage);
    }

    [Fact]
    public async Task AQueuedJobThatNeverWaitedIsNotWaiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Equal(EndInputWaitOutcome.NotWaiting, await Store.EndInputWaitAsync(acceptance.ReportJobId, cancellationToken));
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(state!.Job.Stage);
        Assert.Equal(ReportJobStatus.Queued, state.Job.Status);
    }

    [Fact]
    public async Task TheJobsThatMayBeWaitingOnAnUploadAreFoundInCreationOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var otherUploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);

        var first = Acceptance(uploadId);
        var second = Acceptance(uploadId);
        var third = Acceptance(uploadId);
        var elsewhere = Acceptance(otherUploadId);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(first, Now, cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(second, Now.AddSeconds(1), cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(third, Now.AddSeconds(2), cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(elsewhere, Now, cancellationToken));

        // Nothing has waited yet: a queued job with no stage is one that may be about to.
        Assert.Equal(
            [first.ReportJobId, second.ReportJobId, third.ReportJobId],
            await Store.FindInputWaitingJobIdsAsync(uploadId, cancellationToken));

        Assert.Equal(BeginInputWaitOutcome.Applied, await Store.BeginInputWaitAsync(second.ReportJobId, cancellationToken));
        Assert.Equal(
            [first.ReportJobId, second.ReportJobId, third.ReportJobId],
            await Store.FindInputWaitingJobIdsAsync(uploadId, cancellationToken));

        await Store.FailJobAsync(first.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken);
        Assert.Equal(
            [second.ReportJobId, third.ReportJobId],
            await Store.FindInputWaitingJobIdsAsync(uploadId, cancellationToken));

        Assert.Empty(await Store.FindInputWaitingJobIdsAsync("upload-ffffffffffffffffffffffffffffffff", cancellationToken));
    }

    [Fact]
    public async Task TheReferenceAJobWasAcceptedWithIsReadBackAndAnUnknownJobHasNone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var exact = Acceptance(uploadId);
        var byName = Acceptance(uploadId, version: null);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(exact, Now, cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(byName, Now, cancellationToken));

        var named = await Store.FindTemplateReferenceAsync(exact.ReportJobId, cancellationToken);

        Assert.NotNull(named);
        Assert.Equal(exact.ReportJobId, named!.ReportJobId);
        Assert.Equal("pilot-grouped", named.TemplateId);
        Assert.Equal(TemplateReferenceForm.Exact, named.ReferenceForm);
        Assert.Equal("1.0.0", named.RequestedVersion);

        // A reference by name carries no version: the value a store answers is the one the
        // request wrote, and the migration's check ties a version to an exact reference.
        var unresolved = await Store.FindTemplateReferenceAsync(byName.ReportJobId, cancellationToken);
        Assert.NotNull(unresolved);
        Assert.Equal(TemplateReferenceForm.ByName, unresolved!.ReferenceForm);
        Assert.Null(unresolved.RequestedVersion);

        Assert.Null(await Store.FindTemplateReferenceAsync(
            "job-ffffffffffffffffffffffffffffffff",
            cancellationToken));
    }

    [Fact]
    public async Task AStatusReadAnswersTheOwnerAQueuedJobWithNoStageAndNoTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        Assert.NotNull(status);
        Assert.Equal(acceptance.ReportJobId, status!.ReportJobId);
        Assert.Equal(ReportJobStatus.Queued, status.Status);
        Assert.Null(status.Stage);
        Assert.Equal(0, status.RecordsRead);
        Assert.Null(status.RecordsTotal);
        Assert.Equal(0, status.BlocksCompleted);
        Assert.Null(status.BlocksTotal);
        Assert.Equal(0, status.PagesGenerated);
        Assert.Null(status.ErrorCode);
        Assert.Equal(Now, status.CreatedAt);
        Assert.Null(status.StartedAt);
        Assert.Null(status.FinishedAt);
        var output = Assert.Single(status.Outputs);
        Assert.Equal(OutputFormat.Pdf, output.Format);
        Assert.Equal(OutputStatus.Pending, output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Null(output.ArtifactId);
    }

    [Fact]
    public async Task AStatusReadAnswersNothingForAnotherConsumerOrAnAbsentJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Null(await Store.FindStatusAsync(acceptance.ReportJobId, "another-consumer", cancellationToken));
        Assert.Null(await Store.FindStatusAsync("job-ffffffffffffffffffffffffffffffff", Consumer, cancellationToken));
    }

    [Fact]
    public async Task AStatusReadShowsTheArtifactOfAPublishedOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken);

        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        var output = Assert.Single(status!.Outputs);
        Assert.Equal(OutputStatus.Succeeded, output.Status);
        Assert.Equal(ArtifactId, output.ArtifactId);
        Assert.Null(output.ErrorCode);
    }

    [Fact]
    public async Task AProgressWriteOnlyEverRaisesTheReadCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.Equal(ProgressWriteOutcome.Raised, await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 10, cancellationToken));
        Assert.Equal(ProgressWriteOutcome.Raised, await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 40, cancellationToken));
        Assert.Equal(40, (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.RecordsRead);
        Assert.Equal(ProgressWriteOutcome.Unchanged, await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 25, cancellationToken));
        Assert.Equal(40, (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.RecordsRead);

        // A write above a total already known is ignored, never an error: progress must
        // not fail the step that reports it, and the row stays exactly as it was. The
        // commit itself raises the count to the count it carries.
        await Store.CommitPartitionAsync(Partition(acceptance.ReportJobId, recordCount: 100, blockCount: 2), cancellationToken);
        Assert.Equal(ProgressWriteOutcome.Unchanged, await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 101, cancellationToken));
        var known = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(100, known!.RecordsRead);
        Assert.Equal(100, known.RecordsTotal);

        // A job that ended is frozen: a later write from a stale attempt changes nothing.
        await Store.FailJobAsync(acceptance.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken);
        Assert.Equal(ProgressWriteOutcome.Unchanged, await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 99, cancellationToken));
        Assert.Equal(100, (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.RecordsRead);
    }

    [Fact]
    public async Task ACommitSetsTheTotalsOnceAndRaisesTheReadCountToItsCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 600, cancellationToken);

        var committed = await Store.CommitPartitionAsync(
            Partition(acceptance.ReportJobId, recordCount: 800, blockCount: 3),
            cancellationToken);

        Assert.Equal(PartitionCommitOutcome.Committed, committed.Outcome);
        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(800, status!.RecordsRead);
        Assert.Equal(800, status.RecordsTotal);
        Assert.Equal(3, status.BlocksTotal);

        // A second commit of the same job is a conflict: it changes neither the partition
        // nor the totals the first one wrote.
        var repeated = await Store.CommitPartitionAsync(
            Partition(acceptance.ReportJobId, recordCount: 999, blockCount: 9),
            cancellationToken);
        Assert.Equal(PartitionCommitOutcome.AlreadyCommitted, repeated.Outcome);
        var unchanged = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(800, unchanged!.RecordsTotal);
        Assert.Equal(3, unchanged.BlocksTotal);
    }

    [Fact]
    public async Task ACommitAtTheStoredCountKeepsTheCountAndStillSetsTheTotalsOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.RecordRecordsReadAsync(acceptance.ReportJobId, 800, cancellationToken);

        await Store.CommitPartitionAsync(Partition(acceptance.ReportJobId, recordCount: 800, blockCount: 1), cancellationToken);

        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(800, status!.RecordsRead);
        Assert.Equal(800, status.RecordsTotal);
        Assert.Equal(1, status.BlocksTotal);
    }

    [Fact]
    public async Task ABlockCommitCountsItsBlockAndItsPagesOnlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        Assert.Equal(PartCommitOutcome.Committed, (await Store.CommitPartAsync(Commit(acceptance.OutputId), cancellationToken)).Outcome);
        var first = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(1, first!.BlocksCompleted);
        Assert.Equal(3, first.PagesGenerated);

        Assert.Equal(
            PartCommitOutcome.AlreadyCommitted,
            (await Store.CommitPartAsync(Commit(acceptance.OutputId) with { PageCount = 9 }, cancellationToken)).Outcome);
        var repeated = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(1, repeated!.BlocksCompleted);
        Assert.Equal(3, repeated.PagesGenerated);

        Assert.Equal(
            PartCommitOutcome.Committed,
            (await Store.CommitPartAsync(Commit(acceptance.OutputId) with { BlockIndex = 1, PageCount = 2 }, cancellationToken)).Outcome);
        var second = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(2, second!.BlocksCompleted);
        Assert.Equal(5, second.PagesGenerated);
    }

    [Fact]
    public async Task ATransitionWritesTheStageOfItsTargetAndAnswersTheCreationInstant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var applied = await Store.TransitionJobAsync(
            acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        Assert.Equal(TransitionOutcome.Applied, applied.Outcome);
        Assert.Equal(Now, applied.CreatedAt);
        Assert.Equal(
            ReportJobStage.ValidatingData,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Stage);

        var running = await Store.TransitionJobAsync(
            acceptance.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);
        Assert.Equal(TransitionOutcome.Applied, running.Outcome);
        Assert.Equal(
            ReportJobStage.RenderingPdf,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Stage);

        // A repeated transition wrote nothing, carries no instant and leaves the stage.
        var repeated = await Store.TransitionJobAsync(
            acceptance.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);
        Assert.Equal(TransitionOutcome.AlreadyApplied, repeated.Outcome);
        Assert.Null(repeated.CreatedAt);
        Assert.Equal(
            ReportJobStage.RenderingPdf,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Stage);

        // A transition from a state the job is not in changes neither the state nor the stage.
        var notApplicable = await Store.TransitionJobAsync(
            acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        Assert.Equal(TransitionOutcome.NotApplicable, notApplicable.Outcome);
        var unchanged = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(ReportJobStatus.Running, unchanged!.Status);
        Assert.Equal(ReportJobStage.RenderingPdf, unchanged.Stage);

        Assert.Throws<ArgumentOutOfRangeException>(() => JobStages.StageFor(ReportJobStatus.Succeeded));
    }

    [Fact]
    public async Task ARepeatedFinalisationLeavesTheFirstOutcomeExactlyAsItWas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken);
        var endedAt = Now.AddMinutes(1);

        Assert.Equal(JobFinalizationOutcome.Applied, (await Store.FinalizeJobAsync(acceptance.ReportJobId, endedAt, cancellationToken)).Outcome);
        var first = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(ReportJobStatus.Succeeded, first!.Status);
        Assert.Equal(endedAt, first.FinishedAt);
        Assert.Null(first.ErrorCode);
        Assert.Null(first.Stage);

        Assert.Equal(
            JobFinalizationOutcome.AlreadyTerminal,
            (await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(2), cancellationToken)).Outcome);
        var repeated = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(ReportJobStatus.Succeeded, repeated!.Status);
        Assert.Equal(endedAt, repeated.FinishedAt);
        Assert.Null(repeated.ErrorCode);
    }

    [Fact]
    public async Task AnEmptyPartitionCommitsAKnownZeroTotalAndItsBlock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        await Store.CommitPartitionAsync(Partition(acceptance.ReportJobId, recordCount: 0, blockCount: 1), cancellationToken);

        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(0, status!.RecordsRead);
        Assert.NotNull(status.RecordsTotal);
        Assert.Equal(0, status.RecordsTotal);
        Assert.Equal(1, status.BlocksTotal);

        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        Assert.Equal(
            PartCommitOutcome.Committed,
            (await Store.CommitPartAsync(Commit(acceptance.OutputId) with { PageCount = 1 }, cancellationToken)).Outcome);
        var committed = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(1, committed!.BlocksCompleted);
        Assert.Equal(1, committed.PagesGenerated);
    }

    [Fact]
    public async Task AJobRefusedBeforeItsPartitionKeepsZeroCountersAndNoTotals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        await Store.FailJobAsync(acceptance.ReportJobId, "QUOTA_EXCEEDED", "block", Now, cancellationToken);

        var status = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(ReportJobStatus.Failed, status!.Status);
        Assert.Equal("QUOTA_EXCEEDED", status.ErrorCode);
        Assert.Equal(0, status.RecordsRead);
        Assert.Null(status.RecordsTotal);
        Assert.Equal(0, status.BlocksCompleted);
        Assert.Null(status.BlocksTotal);
        Assert.Null(status.Stage);
    }

    /// <summary>
    /// The taken versions a pin must point at. The in-memory store holds its pins itself;
    /// the database one makes the pin a foreign key, so a case that pins must record the
    /// version first. A store that enforces nothing answers null and needs no seed.
    /// </summary>
    protected virtual ITemplateVersionStore? Versions => null;

    [Fact]
    public async Task AnAcceptedReferenceKeepsTheBaselineItWasGiven()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, version: null) with { BaselineVersion = "1.4.0" };

        var result = await Store.AcceptAsync(acceptance, Now, cancellationToken);

        Assert.IsType<JobAcceptanceResult.Accepted>(result);
        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(reference);
        Assert.Equal(TemplateReferenceForm.ByName, reference!.ReferenceForm);
        Assert.Null(reference.RequestedVersion);
        Assert.Equal("1.4.0", reference.BaselineVersion);
        Assert.Null(reference.Pin);
    }

    [Fact]
    public async Task AnExactReferenceIsAcceptedWithNoBaselineAndNoPin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));

        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.NotNull(reference);
        Assert.Equal(TemplateReferenceForm.Exact, reference!.ReferenceForm);
        Assert.Equal("1.0.0", reference.RequestedVersion);
        Assert.Null(reference.BaselineVersion);
        Assert.Null(reference.Pin);
    }

    [Fact]
    public async Task TheFirstPinIsTheOneTheJobKeeps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.1.0", cancellationToken);
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var first = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken);
        var second = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version, hash: new string('b', 64)), Now.AddSeconds(2), cancellationToken);

        Assert.True(first.WasWrittenByThisCall);
        Assert.Equal(version, first.Pin!.ResolvedVersion);
        Assert.False(second.WasWrittenByThisCall);
        Assert.Equal(Hash, second.Pin!.PackageSha256);
        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(Hash, reference!.Pin!.PackageSha256);
        Assert.Equal(Now.AddSeconds(1), reference.Pin!.ResolvedAt);
    }

    [Fact]
    public async Task APinOnAJobThatEndedIsRefusedAndWritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.1.0", cancellationToken);
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.FailJobAsync(acceptance.ReportJobId, "TEMPLATE_NOT_FOUND", "template-not-found", "The template or template version was not found.", Now, cancellationToken);

        var state = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken);

        Assert.True(state.JobTerminal);
        Assert.Null(state.Pin);
        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(reference!.Pin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The template source could not be reached. The request may be submitted again later.")]
    public async Task AFailureKeepsTheMessageItWasGiven(string? message)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        await Store.FailJobAsync(acceptance.ReportJobId, "TEMPLATE_SOURCE_UNAVAILABLE", "template-source-unavailable", message, Now, cancellationToken);

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Failed, state!.Job.Status);
        Assert.Equal("template-source-unavailable", state.Job.ErrorReason);
        Assert.Equal(message, state.Job.ErrorMessage);
    }

    [Fact]
    public async Task AFailureMessageOfTheLongestLengthIsStoredWhole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        var message = new string('m', 1_000);

        await Store.FailJobAsync(acceptance.ReportJobId, "TEMPLATE_INVALID", "template-invalid", message, Now, cancellationToken);

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(message, state!.Job.ErrorMessage);
    }

    /// <summary>A pin of one version of the pilot template, with the values every case uses.</summary>
    /// <param name="version">The version the pin fixes.</param>
    /// <param name="hash">The package hash, when a case needs a second one.</param>
    protected static TemplatePin PinOf(string version, string? hash = null) => new(
        "pilot-grouped",
        version,
        "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
        hash ?? Hash,
        TemplateEngine.Scriban,
        "7.5.1",
        "schema.json",
        Now.AddSeconds(1));

    /// <summary>Records a taken version when the store makes the pin point at one, and answers its version.</summary>
    /// <param name="version">The version to record.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected async Task<string> TakenAsync(string version, CancellationToken cancellationToken)
    {
        if (Versions is not null)
        {
            await Versions.TryInsertAsync(
                TemplateVersionRecords.Sample(templateId: "pilot-grouped", version: version, owner: Consumer, packageSha256: Hash),
                cancellationToken);
        }

        return version;
    }

    /// <summary>A partition of <paramref name="reportJobId"/>, holding one record.</summary>
    /// <param name="reportJobId">The job it belongs to.</param>
    protected static PartitionRecord Partition(string reportJobId) => new(
        reportJobId,
        1,
        1,
        1,
        24,
        $"index/{reportJobId}/0/attempt",
        Hash,
        120,
        Now);

    /// <summary>A partition of <paramref name="reportJobId"/> with the counts a commit carries.</summary>
    /// <param name="reportJobId">The job it belongs to.</param>
    /// <param name="recordCount">How many records the partition holds.</param>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    protected static PartitionRecord Partition(string reportJobId, long recordCount, int blockCount) => new(
        reportJobId,
        1,
        blockCount,
        recordCount,
        24,
        $"index/{reportJobId}/0/attempt",
        Hash,
        120,
        Now);

    /// <summary>A commit of block 0 of <paramref name="outputId"/>.</summary>
    /// <param name="outputId">The output it belongs to.</param>
    protected static PartCommit Commit(string outputId) => new(
        outputId,
        0,
        $"block/{outputId}/0/attempt",
        Hash,
        1_024,
        3,
        Hash,
        Now);

    /// <summary>The identifier every published artifact of this contract carries.</summary>
    protected const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    /// <summary>An artifact of <paramref name="outputId"/>.</summary>
    /// <param name="reportJobId">The job it belongs to.</param>
    /// <param name="outputId">The output it is the result of.</param>
    protected static ArtifactRecord Artifact(string reportJobId, string outputId) => new(
        ArtifactId,
        reportJobId,
        outputId,
        OutputFormat.Pdf,
        "application/pdf",
        $"outputs/{reportJobId}/{outputId}/attempt",
        Hash,
        2_048,
        3,
        Now);
    /// <summary>An acceptance of a job over <paramref name="uploadId"/>, with identifiers of their own.</summary>
    /// <param name="uploadId">The upload the job reports on.</param>
    /// <param name="consumerId">The consumer the job is accepted for.</param>
    /// <param name="version">The version the acceptance names; null for a reference by name.</param>
    protected JobAcceptance Acceptance(string uploadId, string consumerId = Consumer, string? version = "1.0.0") => new(
        JobIdentifier.Create(),
        JobIdentifier.CreateOutput(),
        consumerId,
        uploadId,
        "pilot-grouped",
        version is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
        version);

    /// <summary>Creates an upload and drives it to <paramref name="state"/> through the upload store's own steps.</summary>
    /// <param name="state">The state the upload must be in.</param>
    /// <param name="consumerId">The owner of the upload.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>The identifier of the upload.</returns>
    protected async Task<string> SeedAsync(
        UploadState state,
        string consumerId = Consumer,
        CancellationToken cancellationToken = default)
    {
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        var record = new UploadRecord(
            uploadId,
            consumerId,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            Now.AddHours(-1),
            Now.AddHours(1));
        await Uploads.CreateAsync(record, cancellationToken);

        if (state is UploadState.Created)
        {
            return uploadId;
        }

        Assert.True(await Uploads.TryStartTransferAsync(
            uploadId, consumerId, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        if (state is UploadState.Uploading)
        {
            return uploadId;
        }

        if (state is UploadState.Rejected)
        {
            Assert.True(await Uploads.RejectTransferAsync(
                uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, 12, Now.AddMinutes(-20), cancellationToken));
            return uploadId;
        }

        if (state is UploadState.Expired)
        {
            Assert.True((await Uploads.ExpireIfOverdueAsync(uploadId, record.ExpiresAt, cancellationToken)).Expired);
            return uploadId;
        }

        Assert.True(await Uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now.AddMinutes(-20), cancellationToken));

        // A verifying upload past its deadline is confirmed just in time and then outlives the
        // deadline; one that is in time keeps a deadline hours ahead of the decision.
        var deadline = state is UploadState.VerifyingPastItsDeadline ? Now : Now.AddHours(5);
        Assert.True(await Uploads.ConfirmAsync(
            uploadId, consumerId, Hash, Confirmed, Now.AddMinutes(-20), deadline, cancellationToken));

        if (state is UploadState.Ready)
        {
            Assert.Equal(
                SnapshotCommitResult.Committed,
                await Uploads.CommitReadyAsync(Snapshot(uploadId, consumerId), Now.AddMinutes(-10), cancellationToken));
        }

        return uploadId;
    }

    /// <summary>A snapshot of <paramref name="uploadId"/> for the ready state.</summary>
    /// <param name="uploadId">The upload the snapshot belongs to.</param>
    /// <param name="consumerId">The owner of the upload.</param>
    protected static SnapshotRecord Snapshot(string uploadId, string consumerId = Consumer) => new(
        uploadId,
        consumerId,
        ObjectKey.FromSegments("snapshots", uploadId, "a"),
        UploadContentEncoding.None,
        DataProfile.HeaderItemsV1,
        Hash,
        900,
        Hash,
        900,
        Now.AddMinutes(-10));

    /// <inheritdoc />
    public async ValueTask InitializeAsync() =>
        (Store, Uploads) = await CreateStoresAsync(TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Store is not null)
        {
            await DisposeStoresAsync(Store, Uploads);
        }
    }
}
