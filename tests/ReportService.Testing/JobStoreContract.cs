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
        Assert.Equal("input-expired", output.ErrorReason);
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
        Assert.Equal("input-expired", output.ErrorReason);
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

    /// <summary>
    /// The commit carries what produced the block's presentation, and the store returns it
    /// with the row: the job's fixed settings, the engine the version declares and the
    /// version it runs, and the globalisation data of the process that printed it. A commit
    /// that names none reads back as none - the row the column's default describes - so no
    /// reader is ever handed a half-filled object.
    /// </summary>
    [Fact]
    public async Task ACommitRecordsThePresentationAndACommitWithoutOneReadsBackWithout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        var presentation = new PresentationProvenance(
            "es-AR",
            "America/Argentina/Buenos_Aires",
            new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero),
            "SCRIBAN",
            "7.5.1",
            "74.2",
            "2025b");

        var committed = await Store.CommitPartAsync(Commit(acceptance.OutputId) with { Presentation = presentation }, cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);

        var read = await Store.FindPartAsync(acceptance.OutputId, 0, cancellationToken);
        Assert.NotNull(read);
        Assert.Equal(presentation, read!.Presentation);

        var without = await Store.CommitPartAsync(Commit(acceptance.OutputId, blockIndex: 1), cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, without.Outcome);
        var none = await Store.FindPartAsync(acceptance.OutputId, 1, cancellationToken);
        Assert.NotNull(none);
        Assert.Null(none!.Presentation);
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

        Assert.Equal(OutputFailureOutcome.Applied, await Store.FailOutputAsync(acceptance.OutputId, "PAGINATION_FAILED", null, Now, cancellationToken));
        Assert.Equal(
            OutputFailureOutcome.AlreadyTerminal,
            await Store.FailOutputAsync(acceptance.OutputId, "PAGINATION_FAILED", null, Now, cancellationToken));

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("PAGINATION_FAILED", state!.Job.ErrorCode);
        Assert.Equal("output-failed", state.Job.ErrorReason);
    }

    [Fact]
    public async Task APartitionsRefusalIsRecordedWithItAndAnsweredOnARepeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        const string Code = "ROW_BOUND_EXCEEDED";
        const string Reason =
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description";
        var partition = Partition(acceptance.ReportJobId) with
        {
            PdfRefusalCode = Code,
            PdfRefusalReason = Reason,
        };

        var first = await Store.CommitPartitionAsync(partition, cancellationToken);
        var repeated = await Store.CommitPartitionAsync(Partition(acceptance.ReportJobId), cancellationToken);
        var read = await Store.FindPartitionAsync(acceptance.ReportJobId, cancellationToken);

        Assert.Equal(PartitionCommitOutcome.Committed, first.Outcome);
        Assert.Equal(Code, first.Existing.PdfRefusalCode);
        Assert.Equal(PartitionCommitOutcome.AlreadyCommitted, repeated.Outcome);
        Assert.Equal(Code, repeated.Existing.PdfRefusalCode);
        Assert.Equal(Reason, repeated.Existing.PdfRefusalReason);
        Assert.Equal(Code, read!.PdfRefusalCode);
        Assert.Equal(Reason, read.PdfRefusalReason);
    }

    [Fact]
    public async Task TheReasonOfAFailedOutputIsReadBackAndCopiedToTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        const string Code = "ROW_BOUND_EXCEEDED";
        const string Reason =
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description";

        Assert.Equal(OutputFailureOutcome.Applied, await Store.FailOutputAsync(acceptance.OutputId, Code, Reason, Now, cancellationToken));
        var failed = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(Reason, Assert.Single(failed!.Outputs).ErrorReason);

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        Assert.Equal(Code, closed.ErrorCode);
        Assert.Equal(Code, state!.Job.ErrorCode);
        Assert.Equal(Reason, state.Job.ErrorReason);
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
    public async Task APublishedArtifactIsListedAndReadBackWithItsExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.OutputId);
        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(artifact, cancellationToken)).Outcome);

        var listed = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        Assert.NotNull(listed);
        Assert.Equal(acceptance.ReportJobId, listed!.ReportJobId);
        var view = Assert.Single(listed.Artifacts);
        Assert.Equal(artifact.ArtifactId, view.ArtifactId);
        Assert.Equal(acceptance.ReportJobId, view.ReportJobId);
        Assert.Equal(artifact.Format, view.Format);
        Assert.Equal(artifact.MediaType, view.MediaType);
        Assert.Equal(artifact.ObjectKey, view.ObjectKey);
        Assert.Equal(artifact.Sha256, view.Sha256);
        Assert.Equal(artifact.SizeBytes, view.SizeBytes);
        Assert.Equal(artifact.CreatedAt, view.CreatedAt);
        Assert.Equal(artifact.ExpiresAt, view.ExpiresAt);

        var found = await Store.FindArtifactAsync(
            acceptance.ReportJobId, artifact.ArtifactId, Consumer, cancellationToken);
        Assert.Equal(view, found);
    }

    [Fact]
    public async Task ARepetitionOfAPublicationKeepsTheExpiryTheFirstOneWrote()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.OutputId);
        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(artifact, cancellationToken)).Outcome);

        // A retried publication carries a later expiry of its own: the row the first one wrote
        // stands, down to the instant it stops being downloadable.
        var repeated = await Store.PublishAsync(
            artifact with { ArtifactId = "artifact-ffffffffffffffffffffffffffffffff", ExpiresAt = Now.AddDays(30) },
            cancellationToken);

        Assert.Equal(PublishOutcome.AlreadyPublished, repeated.Outcome);
        Assert.Equal(ArtifactId, repeated.ArtifactId);
        var view = Assert.Single((await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Artifacts);
        Assert.Equal(Now.AddDays(7), view.ExpiresAt);
    }

    [Fact]
    public async Task AnExpiryBeforeTheCreationOfItsArtifactIsRefusedAndStoresNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);

        // The schema refuses a row that expires before it was created, and so does the double:
        // no store answers a publication that cannot describe a downloadable artifact.
        await Assert.ThrowsAnyAsync<Exception>(() => Store.PublishAsync(
            Artifact(acceptance.ReportJobId, acceptance.OutputId) with { ExpiresAt = Now.AddSeconds(-1) },
            cancellationToken));

        var listed = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.NotNull(listed);
        Assert.Empty(listed!.Artifacts);
    }

    [Fact]
    public async Task AJobThatHasPublishedNothingListsAnEmptySetAndAnotherConsumerSeesNoJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var pending = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(acceptance.ReportJobId, pending!.ReportJobId);
        Assert.Empty(pending.Artifacts);

        Assert.Null(await Store.ListArtifactsAsync(acceptance.ReportJobId, "another-consumer", cancellationToken));
        Assert.Null(await Store.ListArtifactsAsync(
            "job-ffffffffffffffffffffffffffffffff", Consumer, cancellationToken));

        // A job that ended without publishing has published nothing either, and it is still the
        // owner's job: its list is empty rather than absent.
        await Store.FailJobAsync(acceptance.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now, cancellationToken);
        var failed = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.NotNull(failed);
        Assert.Empty(failed!.Artifacts);
    }

    [Fact]
    public async Task AnArtifactIsReadOnlyUnderItsOwnJobOfItsOwnConsumer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        var otherJob = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(otherJob, Now, cancellationToken));
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.OutputId);
        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(artifact, cancellationToken)).Outcome);

        Assert.NotNull(await Store.FindArtifactAsync(
            acceptance.ReportJobId, artifact.ArtifactId, Consumer, cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(
            otherJob.ReportJobId, artifact.ArtifactId, Consumer, cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(
            acceptance.ReportJobId, artifact.ArtifactId, "another-consumer", cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(
            acceptance.ReportJobId, "artifact-ffffffffffffffffffffffffffffffff", Consumer, cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(
            "job-ffffffffffffffffffffffffffffffff", artifact.ArtifactId, Consumer, cancellationToken));
    }

    [Fact]
    public async Task AStatusReadAnswersNothingForAJobInEveryStateOfAnotherConsumer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string other = "another-consumer";

        var queued = await AcceptedAsync(cancellationToken);
        var running = await AcceptedAsync(cancellationToken);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Store.TransitionJobAsync(running.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken)).Outcome);
        Assert.Equal(
            TransitionOutcome.Applied,
            (await Store.TransitionJobAsync(running.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken)).Outcome);
        var failed = await AcceptedAsync(cancellationToken);
        Assert.Equal(
            JobFailureOutcome.Applied,
            await Store.FailJobAsync(failed.ReportJobId, "QUOTA_EXCEEDED", "block", Now, cancellationToken));
        var (succeeded, _) = await PublishedAsync(live: true, cancellationToken);

        foreach (var acceptance in new[] { queued, running, failed, succeeded })
        {
            Assert.NotNull(await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken));
            Assert.Null(await Store.FindStatusAsync(acceptance.ReportJobId, other, cancellationToken));
        }
    }

    [Fact]
    public async Task AListOfAJobWithNoArtifactWithALiveOneOrAnExpiredOneIsNothingForAnotherConsumer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string other = "another-consumer";

        var empty = await AcceptedAsync(cancellationToken);
        var (live, _) = await PublishedAsync(live: true, cancellationToken);
        var (expired, _) = await PublishedAsync(live: false, cancellationToken);

        var ownerEmpty = await Store.ListArtifactsAsync(empty.ReportJobId, Consumer, cancellationToken);
        Assert.NotNull(ownerEmpty);
        Assert.Empty(ownerEmpty!.Artifacts);
        Assert.NotNull(await Store.ListArtifactsAsync(live.ReportJobId, Consumer, cancellationToken));
        Assert.NotNull(await Store.ListArtifactsAsync(expired.ReportJobId, Consumer, cancellationToken));

        foreach (var acceptance in new[] { empty, live, expired })
        {
            // Never an empty view: a foreign job is no job, whatever it holds.
            Assert.Null(await Store.ListArtifactsAsync(acceptance.ReportJobId, other, cancellationToken));
        }
    }

    [Fact]
    public async Task AnArtifactOfAnotherConsumerIsNothingForALiveOneForAnExpiredOneAndUnderEitherJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string other = "another-consumer";

        var (live, liveArtifactId) = await PublishedAsync(live: true, cancellationToken);
        var (expired, expiredArtifactId) = await PublishedAsync(live: false, cancellationToken);
        var otherOwned = Acceptance(
            await SeedAsync(UploadState.Ready, other, cancellationToken),
            other);
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(otherOwned, Now, cancellationToken));
        await Store.TransitionOutputAsync(otherOwned.OutputId, Now, cancellationToken);
        var otherArtifact = ArtifactOf(otherOwned.ReportJobId, otherOwned.OutputId, NextArtifactId(), OutputFormat.Pdf);
        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(otherArtifact, cancellationToken)).Outcome);

        // Another consumer on the owner's job and artifact: live and expired.
        Assert.Null(await Store.FindArtifactAsync(live.ReportJobId, liveArtifactId, other, cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(expired.ReportJobId, expiredArtifactId, other, cancellationToken));

        // The consumer's own job with another consumer's artifact, and the other way round.
        Assert.Null(await Store.FindArtifactAsync(
            live.ReportJobId, otherArtifact.ArtifactId, Consumer, cancellationToken));
        Assert.Null(await Store.FindArtifactAsync(
            otherOwned.ReportJobId, liveArtifactId, Consumer, cancellationToken));
    }

    [Fact]
    public async Task ConsumersWhoseIdentifiersDifferByCaseOrSuffixAreAnsweredOnlyTheirOwn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var consumers = new[] { "consumer-a", "Consumer-A", "consumer-ab" };
        var seeded = new List<(string Consumer, JobAcceptance Acceptance, string ArtifactId)>();

        foreach (var consumerId in consumers)
        {
            var uploadId = await SeedAsync(UploadState.Ready, consumerId, cancellationToken);
            var acceptance = Acceptance(uploadId, consumerId);
            Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
            await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
            var artifact = ArtifactOf(acceptance.ReportJobId, acceptance.OutputId, NextArtifactId(), OutputFormat.Pdf);
            Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(artifact, cancellationToken)).Outcome);
            seeded.Add((consumerId, acceptance, artifact.ArtifactId));
        }

        foreach (var own in seeded)
        {
            Assert.NotNull(await Store.FindStatusAsync(own.Acceptance.ReportJobId, own.Consumer, cancellationToken));
            Assert.NotNull(await Store.ListArtifactsAsync(own.Acceptance.ReportJobId, own.Consumer, cancellationToken));
            Assert.NotNull(await Store.FindArtifactAsync(
                own.Acceptance.ReportJobId, own.ArtifactId, own.Consumer, cancellationToken));

            foreach (var foreign in seeded.Where(entry => !string.Equals(entry.Consumer, own.Consumer, StringComparison.Ordinal)))
            {
                Assert.Null(await Store.FindStatusAsync(foreign.Acceptance.ReportJobId, own.Consumer, cancellationToken));
                Assert.Null(await Store.ListArtifactsAsync(foreign.Acceptance.ReportJobId, own.Consumer, cancellationToken));
                Assert.Null(await Store.FindArtifactAsync(
                    foreign.Acceptance.ReportJobId, foreign.ArtifactId, own.Consumer, cancellationToken));
            }
        }
    }

    /// <summary>Accepts one job over a fresh ready upload and answers its acceptance.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected async Task<JobAcceptance> AcceptedAsync(CancellationToken cancellationToken)
    {
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        return acceptance;
    }

    /// <summary>Accepts one job and publishes its one artifact, live or already expired.</summary>
    /// <param name="live">Whether the artifact's expiry is still ahead of the store's clock.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected async Task<(JobAcceptance Acceptance, string ArtifactId)> PublishedAsync(
        bool live,
        CancellationToken cancellationToken)
    {
        var acceptance = await AcceptedAsync(cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        var artifact = ArtifactOf(acceptance.ReportJobId, acceptance.OutputId, NextArtifactId(), OutputFormat.Pdf);
        if (!live)
        {
            artifact = artifact with { CreatedAt = Now.AddDays(-8), ExpiresAt = Now.AddDays(-1) };
        }

        Assert.Equal(PublishOutcome.Published, (await Store.PublishAsync(artifact, cancellationToken)).Outcome);
        return (acceptance, artifact.ArtifactId);
    }

    /// <summary>A fresh artifact identifier, so two publications of one case never share one.</summary>
    private string NextArtifactId() =>
        "artifact-" + (++_sequence).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheListOrdersItsArtifactsByFormatWhateverTheOrderTheyWerePublishedIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        // The markdown artifact is published first and the html one second: the list still puts
        // html before markdown, the fixed order of the formats and not the order of publication.
        var markdown = ArtifactOf(
            acceptance.ReportJobId,
            JobIdentifier.CreateOutput(),
            "artifact-00000000000000000000000000000002",
            OutputFormat.Markdown);
        var html = ArtifactOf(
            acceptance.ReportJobId,
            JobIdentifier.CreateOutput(),
            "artifact-00000000000000000000000000000001",
            OutputFormat.Html);
        if (!await TryPublishArtifactOfAnotherOutputAsync(markdown, cancellationToken)
            || !await TryPublishArtifactOfAnotherOutputAsync(html, cancellationToken))
        {
            return;
        }

        var listed = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        Assert.NotNull(listed);
        Assert.Equal(
            [html.ArtifactId, markdown.ArtifactId],
            listed!.Artifacts.Select(artifact => artifact.ArtifactId));
    }

    [Fact]
    public async Task AJobPublishedFromAnEmptySnapshotListsItsOneArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.CommitPartitionAsync(
            Partition(acceptance.ReportJobId, recordCount: 0, blockCount: 1), cancellationToken);
        await Store.TransitionOutputAsync(acceptance.OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.OutputId), cancellationToken)).Outcome);

        var listed = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);

        var view = Assert.Single(listed!.Artifacts);
        Assert.Equal(ArtifactId, view.ArtifactId);
        Assert.Equal(OutputFormat.Pdf, view.Format);
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
            (await Store.CommitPartAsync(Commit(acceptance.OutputId, blockIndex: 1) with { PageCount = 2 }, cancellationToken)).Outcome);
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

        // The read carries the reason of the job and of its output, so the answer a consumer
        // reads can name the limit its data exceeded without a second read.
        Assert.Equal("block", status.ErrorReason);
        var output = Assert.Single(status.Outputs);
        Assert.Equal("block", output.ErrorReason);
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
    public async Task AJobAcceptedWithAPresentationKeepsItInTheSpellingItWasGiven()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken)) with
        {
            Culture = "en-us",
            TimeZone = "America/Argentina/Buenos_Aires",
        };

        var result = await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var accepted = Assert.IsType<JobAcceptanceResult.Accepted>(result);
        Assert.Equal("en-us", accepted.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", accepted.Job.TimeZone);

        // The job read and the step's own read of the reference agree: a value is stored once,
        // as the request wrote it, and both reads answer it unchanged.
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("en-us", state!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", state.Job.TimeZone);

        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("en-us", reference!.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", reference.TimeZone);
    }

    [Fact]
    public async Task AJobAcceptedWithoutAPresentationCarriesNone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));

        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(state!.Job.Culture);
        Assert.Null(state.Job.TimeZone);

        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(reference!.Culture);
        Assert.Null(reference.TimeZone);
    }

    [Fact]
    public async Task APresentationOfTheShortestAndLongestLengthIsStoredWhole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shortest = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken)) with
        {
            Culture = "a",
            TimeZone = "z",
        };
        var longest = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken)) with
        {
            Culture = new string('c', 64),
            TimeZone = new string('z', 64),
        };

        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(shortest, Now, cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(longest, Now, cancellationToken));

        var one = await Store.FindForWorkflowAsync(shortest.ReportJobId, cancellationToken);
        Assert.Equal("a", one!.Job.Culture);
        Assert.Equal("z", one.Job.TimeZone);

        var sixtyFour = await Store.FindForWorkflowAsync(longest.ReportJobId, cancellationToken);
        Assert.Equal(new string('c', 64), sixtyFour!.Job.Culture);
        Assert.Equal(new string('z', 64), sixtyFour.Job.TimeZone);
    }

    [Fact]
    public async Task TheFirstPinIsTheOneTheJobKeeps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The job names the version it is pinned to: an exact reference is pinned to exactly
        // the version it asked for, which the database's own check enforces.
        var version = await TakenAsync("1.0.0", cancellationToken);
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
    public async Task ASecondPinOfAnotherShapeNeverReplacesTheFirst()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);

        // A pin in the shape fixed before the object-store source, then one in the shape a
        // version taken from it produces: the first committed pin is the job's, whatever
        // shape the second attempt carries.
        var gitAcceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(gitAcceptance, Now, cancellationToken);
        var gitFirst = await Store.PinTemplateAsync(gitAcceptance.ReportJobId, PinOfGit(version), Now.AddSeconds(1), cancellationToken);
        var storageSecond = await Store.PinTemplateAsync(gitAcceptance.ReportJobId, PinOf(version), Now.AddSeconds(2), cancellationToken);

        Assert.True(gitFirst.WasWrittenByThisCall);
        Assert.False(storageSecond.WasWrittenByThisCall);
        Assert.Equal(gitFirst.Pin!.CommitSha, storageSecond.Pin!.CommitSha);
        Assert.Equal(gitFirst.Pin.PackageSha256, storageSecond.Pin.PackageSha256);
        Assert.Null(storageSecond.Pin.SourceDescriptorSha256);
        Assert.Null(storageSecond.Pin.SourceObjects);

        // And the reverse: a storage-shaped pin first, a legacy one after it.
        var storageAcceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(storageAcceptance, Now, cancellationToken);
        var storageFirst = await Store.PinTemplateAsync(storageAcceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken);
        var gitSecond = await Store.PinTemplateAsync(storageAcceptance.ReportJobId, PinOfGit(version), Now.AddSeconds(2), cancellationToken);

        Assert.True(storageFirst.WasWrittenByThisCall);
        Assert.False(gitSecond.WasWrittenByThisCall);
        Assert.Equal(storageFirst.Pin!.SourceDescriptorSha256, gitSecond.Pin!.SourceDescriptorSha256);
        Assert.Equal(storageFirst.Pin.SourceObjects, gitSecond.Pin.SourceObjects);
        Assert.Null(gitSecond.Pin.CommitSha);
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

    /// <summary>A pin of one version of the pilot template in the shape a version taken from the object store produces.</summary>
    /// <param name="version">The version the pin fixes.</param>
    /// <param name="hash">The package hash, when a case needs a second one.</param>
    protected static TemplatePin PinOf(string version, string? hash = null) => new(
        "pilot-grouped",
        version,
        null,
        TemplateVersionRecords.DescriptorSha256,
        TemplateVersionRecords.Objects,
        hash ?? Hash,
        TemplateEngine.Scriban,
        "7.5.1",
        "schema.json",
        Now.AddSeconds(1));

    /// <summary>A pin of one version of the pilot template in the shape fixed before the object-store source.</summary>
    /// <param name="version">The version the pin fixes.</param>
    /// <param name="hash">The package hash, when a case needs a second one.</param>
    protected static TemplatePin PinOfGit(string version, string? hash = null) => new(
        "pilot-grouped",
        version,
        "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
        null,
        null,
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

    /// <summary>
    /// A commit of one block of <paramref name="outputId"/>: each block's bytes are stored
    /// under a key of their own, so two blocks of one output never share one.
    /// </summary>
    /// <param name="outputId">The output it belongs to.</param>
    /// <param name="blockIndex">The block's index.</param>
    protected static PartCommit Commit(string outputId, int blockIndex) => new(
        outputId,
        blockIndex,
        $"block/{outputId}/{blockIndex}/attempt",
        Hash,
        1_024,
        3,
        Hash,
        Now);

    /// <summary>The identifier every published artifact of this contract carries.</summary>
    protected const string ArtifactId = "artifact-0123456789abcdef0123456789abcdef";

    /// <summary>An artifact of <paramref name="outputId"/>: a PDF of three pages that expires seven days after it is created.</summary>
    /// <param name="reportJobId">The job it belongs to.</param>
    /// <param name="outputId">The output it is the result of.</param>
    protected static ArtifactRecord Artifact(string reportJobId, string outputId) =>
        ArtifactOf(reportJobId, outputId, ArtifactId, OutputFormat.Pdf);

    /// <summary>An artifact of one format, under an identifier of its own, expiring seven days after it is created.</summary>
    /// <param name="reportJobId">The job it belongs to.</param>
    /// <param name="outputId">The output it is the result of.</param>
    /// <param name="artifactId">The identifier it carries.</param>
    /// <param name="format">The format it carries.</param>
    protected static ArtifactRecord ArtifactOf(string reportJobId, string outputId, string artifactId, OutputFormat format) => new(
        artifactId,
        reportJobId,
        outputId,
        format,
        format switch
        {
            OutputFormat.Pdf => "application/pdf",
            OutputFormat.Html => "text/html",
            OutputFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            OutputFormat.Markdown => "text/markdown",
            _ => throw new ArgumentOutOfRangeException(nameof(format), "the format has no media type"),
        },
        $"outputs/{reportJobId}/{outputId}/attempt",
        Hash,
        2_048,
        format is OutputFormat.Pdf ? 3 : null,
        Now,
        Now.AddDays(7));

    /// <summary>
    /// Publishes an artifact of an output the acceptance did not create, when the store can hold
    /// one. A job accepted through the service carries one output only, so two formats in one list
    /// need an output the store is given directly; the double records one, and a store that cannot
    /// be arranged that way answers <see langword="false"/> and the case asserts no order it
    /// cannot present.
    /// </summary>
    /// <param name="artifact">The artifact to publish, naming the output it belongs to.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <returns>Whether the store published the artifact.</returns>
    protected virtual Task<bool> TryPublishArtifactOfAnotherOutputAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

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
