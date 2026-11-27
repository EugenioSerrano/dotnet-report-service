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

    /// <summary>
    /// Creates a job store over the same upload records as <see cref="Store"/>, judging part
    /// commits against <paramref name="capacity"/> instead of the example profile.
    /// </summary>
    /// <param name="capacity">The document capacity the store is configured with.</param>
    protected abstract IJobStore CreateStoreAtCapacity(DocumentCapacity capacity);

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

        // US-008 AC-1: each requested output is materialized as its own independent output row —
        // one PENDING row per requested format, with its own identifier. The acceptance carries
        // an ordered list of requested outputs (AC-1); the store need not preserve that order
        // (no US-008 AC specifies cross-format order, and the real store reads rows back ordered
        // by output_id — SPEC §8.1 D5), so the rows are asserted as a SET: exactly one stored row
        // per requested (id, format), each PENDING, every identifier distinct.
        Assert.Equal(acceptance.Outputs.Count, state.Outputs.Count);
        Assert.Equal(
            acceptance.Outputs.Count,
            state.Outputs.Select(output => output.OutputId).Distinct(StringComparer.Ordinal).Count());
        foreach (var requested in acceptance.Outputs)
        {
            var output = Assert.Single(state.Outputs, row => row.OutputId == requested.OutputId);
            Assert.Equal(requested.Format, output.Format);
            Assert.Equal(OutputStatus.Pending, output.Status);
            Assert.Null(output.ErrorCode);
        }

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
    public async Task AKeyBoundToAJobIsFoundAsTheSameRequestAndAnotherIdentityAsADifferentRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        var same = await Store.FindByIdempotencyKeyAsync(Consumer, "K1", acceptance.Fingerprint, cancellationToken);
        var different = await Store.FindByIdempotencyKeyAsync(
            Consumer,
            "K1",
            RequestIdentity(Consumer, await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken), "1.0.0"),
            cancellationToken);

        Assert.Equal(acceptance.ReportJobId, Assert.IsType<IdempotencyLookup.SameRequest>(same).ReportJobId);
        Assert.IsType<IdempotencyLookup.DifferentRequest>(different);
        Assert.IsType<IdempotencyLookup.None>(
            await Store.FindByIdempotencyKeyAsync(Consumer, "K2", acceptance.Fingerprint, cancellationToken));
    }

    [Fact]
    public async Task AnotherConsumerUsingTheSameKeyIsAnsweredNoneAndItsOwnAcceptanceCreatesItsJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken), idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(first, Now, cancellationToken));

        var otherConsumer = "other-consumer";
        Assert.IsType<IdempotencyLookup.None>(
            await Store.FindByIdempotencyKeyAsync(otherConsumer, "K1", first.Fingerprint, cancellationToken));

        var second = Acceptance(
            await SeedAsync(UploadState.Ready, otherConsumer, cancellationToken),
            otherConsumer,
            idempotencyKey: "K1");
        var result = await Store.AcceptAsync(second, Now, cancellationToken);

        Assert.Equal(second.ReportJobId, Assert.IsType<JobAcceptanceResult.Accepted>(result).Job.ReportJobId);
        Assert.NotEqual(first.ReportJobId, second.ReportJobId);
        Assert.Equal(
            second.ReportJobId,
            Assert.IsType<IdempotencyLookup.SameRequest>(
                await Store.FindByIdempotencyKeyAsync(otherConsumer, "K1", second.Fingerprint, cancellationToken)).ReportJobId);
    }

    [Fact]
    public async Task ARepeatedAcceptanceAnswersTheFirstJobAndLeavesItExactlyAsItWas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        var job = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);

        // The repeat is built with the same identity and new identifiers: it must not write a
        // second job, and the first job's own row is the one that stays.
        var repeat = acceptance with { ReportJobId = JobIdentifier.Create(), Outputs = [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)] };
        var result = await Store.AcceptAsync(repeat, Now.AddMinutes(5), cancellationToken);

        Assert.Equal(acceptance.ReportJobId, Assert.IsType<JobAcceptanceResult.SameRequest>(result).ReportJobId);
        var after = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(job!.Job, after!.Job);
        Assert.Equal(job.UploadStatus, after.UploadStatus);
        Assert.Equal(job.Outputs.Select(output => output.OutputId), after.Outputs.Select(output => output.OutputId));
        Assert.Equal(reference, await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task AConflictingAcceptanceIsAnsweredDifferentRequestAndLeavesTheFirstJobExactlyAsItWas()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));
        var job = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);

        var other = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var conflict = Acceptance(other, idempotencyKey: "K1");
        var result = await Store.AcceptAsync(conflict, Now, cancellationToken);

        Assert.IsType<JobAcceptanceResult.DifferentRequest>(result);
        Assert.Null(await Store.FindForWorkflowAsync(conflict.ReportJobId, cancellationToken));
        var after = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(job!.Job, after!.Job);
        Assert.Equal(job.UploadStatus, after.UploadStatus);
        Assert.Equal(job.Outputs.Select(output => output.OutputId), after.Outputs.Select(output => output.OutputId));
        Assert.Equal(reference, await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ARepeatIsStillAnsweredAfterTheFirstJobsUploadWasRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        Assert.True(await Uploads.CommitRejectedAsync(
            uploadId,
            UploadRejectionCode.DataInvalid,
            UploadRejectionReason.Truncated,
            Now.AddMinutes(1),
            cancellationToken));

        var result = await Store.AcceptAsync(
            acceptance with { ReportJobId = JobIdentifier.Create() }, Now.AddMinutes(2), cancellationToken);

        Assert.Equal(acceptance.ReportJobId, Assert.IsType<JobAcceptanceResult.SameRequest>(result).ReportJobId);
    }

    [Fact]
    public async Task ARepeatIsStillAnsweredAfterTheFirstJobsUploadPassedItsExpiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Verifying, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        // The upload expires an hour after the acceptance; the repeat is made two hours later,
        // when the upload is unusable to a request that would look at it.
        var result = await Store.AcceptAsync(acceptance, Now.AddHours(2), cancellationToken);

        Assert.Equal(acceptance.ReportJobId, Assert.IsType<JobAcceptanceResult.SameRequest>(result).ReportJobId);
    }

    [Fact]
    public async Task ABoundKeyIsAnsweredBeforeAnAbsentOrNotOwnedUpload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var acceptance = Acceptance(uploadId, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(acceptance, Now, cancellationToken));

        // The key is bound and the request is the one that bound it, though it now names an
        // upload this consumer does not have: a bound key is answered its job, never a refusal
        // about the upload. (The identity is passed as the same one on purpose: it is what the
        // first request wrote, and it is the key's answer this case pins, not the reference.)
        var absent = "upload-" + new string('f', 32);
        var sameRequest = acceptance with
        {
            UploadId = absent,
            ReportJobId = JobIdentifier.Create(),
            Outputs = [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
        };
        Assert.Equal(
            acceptance.ReportJobId,
            Assert.IsType<JobAcceptanceResult.SameRequest>(
                await Store.AcceptAsync(sameRequest, Now, cancellationToken)).ReportJobId);

        // A different request under the same key, naming an upload of another consumer: the
        // key's answer is the conflict, not the upload's.
        var foreign = await SeedAsync(UploadState.Ready, "another-consumer", cancellationToken);
        var conflict = Acceptance(foreign, idempotencyKey: "K1");
        Assert.IsType<JobAcceptanceResult.DifferentRequest>(await Store.AcceptAsync(conflict, Now, cancellationToken));

        // Without a key, the same absent reference is the unusable upload a first request is
        // answered with.
        Assert.IsType<JobAcceptanceResult.UploadUnusable>(
            await Store.AcceptAsync(Acceptance(absent), Now, cancellationToken));

        // Neither refused request bound anything, and the first job is still the key's row.
        Assert.Equal(
            acceptance.ReportJobId,
            Assert.IsType<IdempotencyLookup.SameRequest>(
                await Store.FindByIdempotencyKeyAsync(Consumer, "K1", acceptance.Fingerprint, cancellationToken))
                .ReportJobId);
    }

    [Fact]
    public async Task AJobWithoutAKeyIsAcceptedAndTwoSuchJobsCoexist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var first = Acceptance(uploadId);
        var second = Acceptance(uploadId);

        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(first, Now, cancellationToken));
        Assert.IsType<JobAcceptanceResult.Accepted>(await Store.AcceptAsync(second, Now, cancellationToken));

        Assert.NotNull(await Store.FindForWorkflowAsync(first.ReportJobId, cancellationToken));
        Assert.NotNull(await Store.FindForWorkflowAsync(second.ReportJobId, cancellationToken));
    }

    [Fact]
    public async Task ParallelAcceptionsOfOneRequestUnderOneKeyCreateOneJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken);
        var first = Acceptance(uploadId, idempotencyKey: "K1");

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => first with { ReportJobId = JobIdentifier.Create(), Outputs = [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)] })
            .ToArray();

        var results = await Task.WhenAll(
            attempts.Select(attempt => Task.Run(() => Store.AcceptAsync(attempt, Now, cancellationToken), cancellationToken)));

        var accepted = results.OfType<JobAcceptanceResult.Accepted>().ToArray();
        Assert.Single(accepted);
        Assert.All(
            results.OfType<JobAcceptanceResult.SameRequest>(),
            answer => Assert.Equal(accepted[0].Job.ReportJobId, answer.ReportJobId));
        Assert.Equal(31, results.OfType<JobAcceptanceResult.SameRequest>().Count());
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

        Assert.Equal(TransitionOutcome.Applied, await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken));
        Assert.Equal(TransitionOutcome.AlreadyApplied, await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken));
    }

    [Fact]
    public async Task ABlockIsCommittedOnlyForARunningOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var refused = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId), cancellationToken);
        Assert.Equal(PartCommitOutcome.GuardRefused, refused.Outcome);
        Assert.Null(await Store.FindPartAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken));

        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        var committed = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId), cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);
        Assert.Equal(Hash, committed.Existing!.Sha256);

        var again = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId) with { PageCount = 9 }, cancellationToken);
        Assert.Equal(PartCommitOutcome.AlreadyCommitted, again.Outcome);
        Assert.Equal(3, again.Existing!.PageCount);
    }

    /// <summary>
    /// The ordered read the assembly consumes: every commit of an output, ordered by
    /// block index whatever order the renders committed them in.
    /// </summary>
    [Fact]
    public async Task TheOrderedPartReadReturnsTheCommitsByBlockIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        foreach (var index in new[] { 2, 0, 1 })
        {
            var committed = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId, blockIndex: index), cancellationToken);
            Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);
        }

        var parts = await Store.ListPartsAsync(acceptance.Outputs[0].OutputId, cancellationToken);

        Assert.Equal([0, 1, 2], parts.Select(part => part.BlockIndex));
    }

    /// <summary>
    /// The example capacity at the commit, when the deployment configures none: a commit at
    /// the example's pages succeeds, the first commit that would pass it writes no row and
    /// fails the output with the limit, its value and the reached value - never a data value.
    /// </summary>
    [Fact]
    public async Task WithNoConfiguredCapacityACommitThatWouldPassTheExampleIsRefusedAndFailsTheOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        var atLimit = await Store.CommitPartAsync(
            Commit(acceptance.Outputs[0].OutputId, blockIndex: 0) with { PageCount = DocumentCapacity.ExamplePages },
            cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, atLimit.Outcome);

        var over = await Store.CommitPartAsync(
            Commit(acceptance.Outputs[0].OutputId, blockIndex: 1) with { PageCount = 1 },
            cancellationToken);

        Assert.Equal(PartCommitOutcome.LimitExceeded, over.Outcome);
        Assert.Equal(DocumentCapacity.PagesLimit, over.Limit);
        Assert.Equal(DocumentCapacity.ExamplePages, over.LimitValue);
        Assert.Equal(DocumentCapacity.ExamplePages + 1, over.ReachedValue);
        Assert.Null(await Store.FindPartAsync(acceptance.Outputs[0].OutputId, 1, cancellationToken));

        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        var output = Assert.Single(state!.Outputs);
        Assert.Equal(OutputStatus.Failed, output.Status);
        Assert.Equal("OUTPUT_LIMIT_EXCEEDED", output.ErrorCode);
        Assert.Contains("limit=document-pages", output.ErrorReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured page capacity at the commit: commits that reach exactly the configured
    /// pages succeed, and the first commit that would pass them is refused with the
    /// configured value - not the example's - and fails the output.
    /// </summary>
    [Fact]
    public async Task ACommitAtTheConfiguredPagesSucceedsAndOnePageMoreIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStoreAtCapacity(new DocumentCapacity(5, long.MaxValue, "contract-pages"));
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await store.AcceptAsync(acceptance, Now, cancellationToken);
        var outputId = acceptance.Outputs[0].OutputId;
        await store.TransitionOutputAsync(outputId, Now, cancellationToken);

        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(Commit(outputId, 0) with { PageCount = 3 }, cancellationToken)).Outcome);
        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(Commit(outputId, 1) with { PageCount = 2 }, cancellationToken)).Outcome);
        var over = await store.CommitPartAsync(Commit(outputId, 2) with { PageCount = 1 }, cancellationToken);

        Assert.Equal(PartCommitOutcome.LimitExceeded, over.Outcome);
        Assert.Equal(DocumentCapacity.PagesLimit, over.Limit);
        Assert.Equal(5, over.LimitValue);
        Assert.Equal(6, over.ReachedValue);
        Assert.Null(await store.FindPartAsync(outputId, 2, cancellationToken));
        var output = Assert.Single((await store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Outputs);
        Assert.Equal(OutputStatus.Failed, output.Status);
        Assert.Equal("OUTPUT_LIMIT_EXCEEDED", output.ErrorCode);
        Assert.Equal("limit=document-pages limitValue=5 reached=6", output.ErrorReason);
    }

    /// <summary>
    /// A configured parts-bytes capacity at the commit: commits that reach exactly the
    /// configured bytes succeed, and one byte more is refused with the configured value.
    /// </summary>
    [Fact]
    public async Task ACommitAtTheConfiguredBytesSucceedsAndOneByteMoreIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStoreAtCapacity(new DocumentCapacity(int.MaxValue, 2_048, "contract-bytes"));
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await store.AcceptAsync(acceptance, Now, cancellationToken);
        var outputId = acceptance.Outputs[0].OutputId;
        await store.TransitionOutputAsync(outputId, Now, cancellationToken);

        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(Commit(outputId, 0) with { SizeBytes = 1_024 }, cancellationToken)).Outcome);
        Assert.Equal(PartCommitOutcome.Committed, (await store.CommitPartAsync(Commit(outputId, 1) with { SizeBytes = 1_024 }, cancellationToken)).Outcome);
        var over = await store.CommitPartAsync(Commit(outputId, 2) with { SizeBytes = 1 }, cancellationToken);

        Assert.Equal(PartCommitOutcome.LimitExceeded, over.Outcome);
        Assert.Equal(DocumentCapacity.BytesLimit, over.Limit);
        Assert.Equal(2_048, over.LimitValue);
        Assert.Equal(2_049, over.ReachedValue);
    }

    /// <summary>
    /// The capacity is the configuration, not a constant: the same commits pass under one
    /// configured value and are refused under another.
    /// </summary>
    [Fact]
    public async Task TheSameCommitsPassOneConfiguredCapacityAndAreRefusedByAnother()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outcomes = new List<PartCommitOutcome>();
        foreach (var pages in new[] { 4, 3 })
        {
            var store = CreateStoreAtCapacity(new DocumentCapacity(pages, long.MaxValue, "contract-" + pages));
            var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
            await store.AcceptAsync(acceptance, Now, cancellationToken);
            var outputId = acceptance.Outputs[0].OutputId;
            await store.TransitionOutputAsync(outputId, Now, cancellationToken);
            await store.CommitPartAsync(Commit(outputId, 0) with { PageCount = 2 }, cancellationToken);
            outcomes.Add((await store.CommitPartAsync(Commit(outputId, 1) with { PageCount = 2 }, cancellationToken)).Outcome);
        }

        Assert.Equal([PartCommitOutcome.Committed, PartCommitOutcome.LimitExceeded], outcomes);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        var presentation = new PresentationProvenance(
            "es-AR",
            "America/Argentina/Buenos_Aires",
            new DateTimeOffset(2026, 3, 1, 12, 30, 0, TimeSpan.Zero),
            "SCRIBAN",
            "7.5.1",
            "74.2",
            "2025b");

        var committed = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId) with { Presentation = presentation }, cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, committed.Outcome);

        var read = await Store.FindPartAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken);
        Assert.NotNull(read);
        Assert.Equal(presentation, read!.Presentation);

        var without = await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId, blockIndex: 1), cancellationToken);
        Assert.Equal(PartCommitOutcome.Committed, without.Outcome);
        var none = await Store.FindPartAsync(acceptance.Outputs[0].OutputId, 1, cancellationToken);
        Assert.NotNull(none);
        Assert.Null(none!.Presentation);
    }

    [Fact]
    public async Task PublicationSucceedsTheOutputOnceAndClosesTheJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        var published = await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken);
        Assert.Equal(PublishOutcome.Published, published.Outcome);
        Assert.Equal(ArtifactId, published.ArtifactId);

        var again = await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        Assert.Equal(OutputFailureOutcome.Applied, await Store.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", null, Now, cancellationToken));
        Assert.Equal(
            OutputFailureOutcome.AlreadyTerminal,
            await Store.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", null, Now, cancellationToken));

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("PAGINATION_FAILED", state!.Job.ErrorCode);
        Assert.Equal("output-failed", state.Job.ErrorReason);
    }

    // ----- ADR-048 job terminal-status rollup (US-008.BOLT-002, SPEC-261125-1224 §8.1, Phase
    //       D.1/D.3/D.6). The oracles are the approved ADR-048 rules R0–R4 and the guard split,
    //       never the implementation (G06). A store that cannot arrange a multi-output or a
    //       cancelled state answers its seam false and the case asserts nothing it cannot present.

    /// <summary>
    /// R2 (ADR-048): a job that mixes a succeeded output and a failed output ends
    /// <c>PartiallySucceeded</c> — the distinct tri-state terminal value — and carries no
    /// job-level error code or reason (each failed output keeps its own code on itself, ADR-048 §4).
    /// This is the former contested html+pdf case: a succeeded PDF stays published while the HTML
    /// output failed.
    /// </summary>
    [Fact]
    public async Task AMixedJobWhereSomeOutputsSucceededAndSomeFailedEndsPartiallySucceeded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Failed, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.PartiallySucceeded, closed.Status);
        Assert.Null(closed.ErrorCode);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.PartiallySucceeded, state!.Job.Status);
        Assert.True(state.Job.IsTerminal);
        Assert.Null(state.Job.ErrorCode);
        Assert.Null(state.Job.ErrorReason);
        // The succeeded output stays succeeded and downloadable; the failed one keeps its code.
        Assert.Equal(
            OutputStatus.Succeeded,
            Assert.Single(state.Outputs, output => output.OutputId == acceptance.Outputs[0].OutputId).Status);
        Assert.Equal(OutputStatus.Failed, Assert.Single(state.Outputs, output => output.OutputId == html).Status);
    }

    /// <summary>R1 (ADR-048): a multi-output job whose every output succeeded ends <c>Succeeded</c> with no code.</summary>
    [Fact]
    public async Task AMultiOutputJobWhereEveryOutputSucceededEndsSucceeded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Succeeded, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Succeeded, closed.Status);
        Assert.Null(closed.ErrorCode);
        Assert.Null((await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Job.ErrorCode);
    }

    /// <summary>R3 (ADR-048): a multi-output job where no output succeeded ends <c>Failed</c> with a failed output's code.</summary>
    [Fact]
    public async Task AMultiOutputJobWhereNoOutputSucceededEndsFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            OutputFailureOutcome.Applied,
            await Store.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", null, Now, cancellationToken));

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Failed, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closed.Status);
        Assert.NotNull(closed.ErrorCode);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Failed, state!.Job.Status);
        Assert.NotNull(state.Job.ErrorCode);
    }

    /// <summary>
    /// R0 (ADR-048): a job being cancelled (status <c>Cancelling</c>) whose outputs are all
    /// terminal and mixed ends <c>Cancelled</c> — R0 overrides R2. Evaluated after the
    /// already-terminal guard, so the only non-terminal status it fires under is <c>Cancelling</c>.
    /// </summary>
    [Fact]
    public async Task ACancellingJobWithMixedTerminalOutputsEndsCancelledOverridingPartiallySucceeded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Failed, cancellationToken)
            || !await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Null(closed.ErrorCode);
        Assert.Equal(
            ReportJobStatus.Cancelled,
            (await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Job.Status);
    }

    /// <summary>R4 (ADR-048): a job whose every requested output was cancelled ends <c>Cancelled</c>.</summary>
    [Fact]
    public async Task AJobWhoseEveryOutputWasCancelledEndsCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var html = JobIdentifier.CreateOutput();
        if (!await TryCancelExistingOutputAsync(acceptance.Outputs[0].OutputId, cancellationToken)
            || !await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Cancelled, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Null(closed.ErrorCode);
    }

    /// <summary>
    /// The guard split (ADR-048 §3.4, Phase D.3): while any requested output is still non-terminal
    /// the job is not finalised — the rollup waits for every output to be terminal, instead of
    /// finalising early to Failed on the first failure. A succeeded output beside a still-running
    /// one leaves the job in flight.
    /// </summary>
    [Fact]
    public async Task AJobWithAnOutputStillRunningIsNotFinalisedUntilEveryOutputIsTerminal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Running, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.AlreadyTerminal, closed.Outcome);
        Assert.Null(closed.Status);
        Assert.False((await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Job.IsTerminal);
    }

    // ----- US-010 AC-3 / AC-4 + ADR-012 §3.10(3) + ADR-048 §3.1 R0/R4, §3.4: the cancellation arm
    //       of the finalisation and the no-failure-after-cancel guard. The oracles are the approved
    //       US-010 criteria and the accepted ADR-048 rules, never the implementation (G06): a job
    //       that was cancelled closes CANCELLED by R0 whatever its outputs' mix, every output that
    //       had not reached a terminal state ends CANCELLED, an output that had already succeeded
    //       keeps its status and its artifact, and nothing writes FAILED after the cancellation.
    //       A store that cannot arrange a state answers its seam false and the case asserts nothing
    //       it cannot present.

    /// <summary>
    /// US-010 AC-3/AC-4: a job being cancelled whose outputs had not ended closes <c>Cancelled</c>,
    /// and every such output ends <c>Cancelled</c> with no error code — the finalisation owns them,
    /// so the "every output terminal" wait does not hold the job (ADR-012 §3.10(3)).
    /// </summary>
    [Fact]
    public async Task AFinalisationOfACancellingJobWritesItsUnfinishedOutputsCancelledAndTheJobCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Running, cancellationToken)
            || !await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Null(closed.ErrorCode);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Cancelled, state!.Job.Status);
        Assert.True(state.Job.IsTerminal);
        Assert.NotNull(state.Job.FinishedAt);
        Assert.All(state.Outputs, output => Assert.Equal(OutputStatus.Cancelled, output.Status));
        Assert.All(state.Outputs, output => Assert.Null(output.ErrorCode));
        Assert.All(state.Outputs, output => Assert.NotNull(output.FinishedAt));

        // The closure is written once: a repeat of the finalisation changes nothing.
        Assert.Equal(
            JobFinalizationOutcome.AlreadyTerminal,
            (await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(2), cancellationToken)).Outcome);
    }

    /// <summary>
    /// US-010 AC-4 + ADR-048 R0 over R2: a cancelled job that had already succeeded one output
    /// rolls up <c>Cancelled</c> — never <c>PartiallySucceeded</c> — while that output keeps
    /// <c>Succeeded</c> and its published artifact, and the output that had not ended ends
    /// <c>Cancelled</c>.
    /// </summary>
    [Fact]
    public async Task ACancellingJobThatAlreadySucceededOneOutputKeepsItAndStillRollsUpCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

        var html = JobIdentifier.CreateOutput();
        if (!await TrySeedOutputAsync(acceptance.ReportJobId, html, OutputFormat.Html, OutputStatus.Running, cancellationToken)
            || !await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Null(closed.ErrorCode);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Cancelled, state!.Job.Status);
        Assert.Equal(
            OutputStatus.Succeeded,
            Assert.Single(state.Outputs, output => output.OutputId == acceptance.Outputs[0].OutputId).Status);
        Assert.Equal(OutputStatus.Cancelled, Assert.Single(state.Outputs, output => output.OutputId == html).Status);

        // AC-5's store-side fact: the artifact published before the cancellation stays published
        // and readable by its owner.
        var artifacts = await Store.ListArtifactsAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.NotNull(artifacts);
        Assert.Equal(ArtifactId, Assert.Single(artifacts!.Artifacts).ArtifactId);
    }

    /// <summary>
    /// US-010 AC-3 + ADR-048 R0: a cancelled job that requests no output also closes
    /// <c>Cancelled</c> — the "every output terminal" wait is not applied to a cancelled job, and
    /// R0 is decided by the job's own state, not by a count.
    /// </summary>
    [Fact]
    public async Task ACancellingJobThatRequestsNoOutputIsClosedCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        if (!await TryRemoveOutputsAsync(acceptance.ReportJobId, cancellationToken)
            || !await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Equal(
            ReportJobStatus.Cancelled,
            (await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken))!.Job.Status);
    }

    /// <summary>
    /// US-010 AC-4 + ADR-048 R0: after the job was cancelled no output is failed. Ending an output
    /// of a job being cancelled writes nothing and answers <c>JobCancelling</c>; the output keeps
    /// its non-terminal state for the cancellation rollup.
    /// </summary>
    [Fact]
    public async Task FailingAnOutputOfACancellingJobWritesNothingAndSaysWhy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        if (!await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var outcome = await Store.FailOutputAsync(acceptance.Outputs[0].OutputId, "PAGINATION_FAILED", null, Now, cancellationToken);

        Assert.Equal(OutputFailureOutcome.JobCancelling, outcome);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Cancelling, state!.Job.Status);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(OutputStatus.Running, output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Null(output.FinishedAt);
    }

    /// <summary>
    /// US-010 AC-4 + ADR-048 R0: failing the job itself while it is being cancelled writes
    /// nothing — the job stays <c>Cancelling</c> and no output is failed — so a rejected input,
    /// an expired upload or a refused template racing the cancellation cannot end the job
    /// <c>Failed</c> after the cancellation won.
    /// </summary>
    [Fact]
    public async Task FailingACancellingJobWritesNothingAtAll()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);
        if (!await TrySeedCancellingJobAsync(acceptance.ReportJobId, cancellationToken))
        {
            return;
        }

        var outcome = await Store.FailJobAsync(acceptance.ReportJobId, "QUOTA_EXCEEDED", "block", Now, cancellationToken);

        Assert.Equal(JobFailureOutcome.AlreadyTerminal, outcome);
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal(ReportJobStatus.Cancelling, state!.Job.Status);
        Assert.Null(state.Job.ErrorCode);
        Assert.Null(state.Job.FinishedAt);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(OutputStatus.Pending, output.Status);
        Assert.Null(output.ErrorCode);
    }

    /// <summary>
    /// ADR-012 §3.11(5): the closure of a cancelled job reports the instant the cancellation was
    /// requested — the durable request the cancel surface recorded — so the latency is measured
    /// from the consumer's request, while a job that was not cancelled reports none.
    /// </summary>
    [Fact]
    public async Task TheClosureOfACancelledJobReportsWhenTheCancellationWasRequested()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        var requestedAt = Now.AddSeconds(1);
        Assert.IsType<CancellationRequestResult.Transitioned>(
            await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, requestedAt, cancellationToken));

        var closed = await Store.FinalizeJobAsync(acceptance.ReportJobId, Now.AddMinutes(1), cancellationToken);

        Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
        Assert.Equal(ReportJobStatus.Cancelled, closed.Status);
        Assert.Equal(requestedAt, closed.CancellationRequestedAt);

        var other = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(other, Now, cancellationToken);
        await Store.TransitionOutputAsync(other.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(other.ReportJobId, other.Outputs[0].OutputId), cancellationToken)).Outcome);
        var normal = await Store.FinalizeJobAsync(other.ReportJobId, Now.AddMinutes(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, normal.Outcome);
        Assert.Null(normal.CancellationRequestedAt);
    }

    // ----- US-010 AC-1 / AC-6 + ADR-012 §3.10(1): the cancel-request store operation
    //       (RequestCancellationAsync). The oracles are the approved AC-1/AC-6 and the ADR-012
    //       §3.10(1) idempotent-cancel semantics, never the implementation (G06). Both stores
    //       prove the one guarded transition that moves a non-terminal job to CANCELLING, the
    //       single durable cancel intent, the idempotent repeat and terminal no-op, the
    //       owner-scoped not-found, and the two races the SPEC names. The single-intent property
    //       is expressed through the classification: a first cancel is Transitioned (it writes the
    //       one intent); a repeat is AlreadyCancelling (ON CONFLICT DO NOTHING — no second intent
    //       is written). The intent row count itself is asserted where a store exposes it — the
    //       endpoint hermetic test over the in-memory intents list, and the e2e test over the real
    //       report_service.intents table.

    [Fact]
    public async Task RequestingCancellationOfANonTerminalJobMovesItToCancellingAndReturnsTheCurrentState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        var result = await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken);

        // AC-1: the request transitions a QUEUED job to CANCELLING and returns its current state.
        var transitioned = Assert.IsType<CancellationRequestResult.Transitioned>(result);
        Assert.Equal(acceptance.ReportJobId, transitioned.View.ReportJobId);
        Assert.Equal(ReportJobStatus.Cancelling, transitioned.View.Status);
        Assert.False(transitioned.View.FinishedAt.HasValue);
        // A subsequent read agrees: the transition is durable.
        Assert.Equal(
            ReportJobStatus.Cancelling,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Status);
    }

    [Fact]
    public async Task RequestingCancellationOfAJobAlreadyRunningMovesItToCancelling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Queued], ReportJobStatus.Validating, Now, cancellationToken);
        await Store.TransitionJobAsync(acceptance.ReportJobId, [ReportJobStatus.Validating], ReportJobStatus.Running, Now, cancellationToken);

        var result = await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken);

        // AC-1: a job past QUEUED (RUNNING) is cancellable on the same guarded transition.
        Assert.Equal(ReportJobStatus.Cancelling, Assert.IsType<CancellationRequestResult.Transitioned>(result).View.Status);
    }

    [Fact]
    public async Task ASecondCancellationWhileCancellingIsIdempotentAndWritesNoSecondIntent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        Assert.IsType<CancellationRequestResult.Transitioned>(
            await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken));

        var repeat = await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(2), cancellationToken);

        // AC-1 idempotent repeat (ADR-012 §3.10(1)): the guard excludes CANCELLING, so a repeat is
        // AlreadyCancelling — no second intent is written — the status does not regress, and the
        // current state is returned.
        var already = Assert.IsType<CancellationRequestResult.AlreadyCancelling>(repeat);
        Assert.Equal(ReportJobStatus.Cancelling, already.View.Status);
        Assert.Equal(
            ReportJobStatus.Cancelling,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Status);
    }

    [Fact]
    public async Task RequestingCancellationOfATerminalJobIsAnIdempotentNoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // A failed job: the no-op returns the unchanged terminal state, no intent is written.
        var failed = await AcceptedAsync(cancellationToken);
        await Store.FailJobAsync(failed.ReportJobId, "QUOTA_EXCEEDED", "block", Now, cancellationToken);
        var onFailed = await Store.RequestCancellationAsync(failed.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken);
        var terminalFailed = Assert.IsType<CancellationRequestResult.AlreadyTerminal>(onFailed);
        Assert.Equal(ReportJobStatus.Failed, terminalFailed.View.Status);
        Assert.Equal("QUOTA_EXCEEDED", terminalFailed.View.ErrorCode);
        Assert.Equal(ReportJobStatus.Failed, (await Store.FindStatusAsync(failed.ReportJobId, Consumer, cancellationToken))!.Status);

        // A succeeded job: the same idempotent no-op (AC-6).
        var (succeeded, _) = await PublishedAsync(live: true, cancellationToken);
        await Store.FinalizeJobAsync(succeeded.ReportJobId, Now.AddMinutes(1), cancellationToken);
        var onSucceeded = await Store.RequestCancellationAsync(succeeded.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken);
        Assert.Equal(
            ReportJobStatus.Succeeded,
            Assert.IsType<CancellationRequestResult.AlreadyTerminal>(onSucceeded).View.Status);
    }

    [Fact]
    public async Task RequestingCancellationOfAForeignOrAbsentJobIsNotFoundAndChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        // Owner-scoped: a job of another consumer answers the same as a job that does not exist —
        // no existence leak — and neither call moves the job.
        Assert.IsType<CancellationRequestResult.NotFound>(
            await Store.RequestCancellationAsync(acceptance.ReportJobId, "another-consumer", Now.AddSeconds(1), cancellationToken));
        Assert.IsType<CancellationRequestResult.NotFound>(
            await Store.RequestCancellationAsync("job-ffffffffffffffffffffffffffffffff", Consumer, Now.AddSeconds(1), cancellationToken));
        Assert.NotEqual(
            ReportJobStatus.Cancelling,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Status);
    }

    [Fact]
    public async Task AJobThatReachedTerminalBeforeTheCancelIsNotMoved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        // The cancel/completion race (ADR-012 §3.10(5)): of a cancellation and a completion,
        // whichever commits first wins. A job that reached terminal before the cancel is not moved
        // — the guarded UPDATE matches no row — and the cancel is the terminal no-op.
        await Store.FailJobAsync(acceptance.ReportJobId, "EXECUTION_FAILED", "lost-execution", Now, cancellationToken);
        var result = await Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken);
        Assert.Equal(ReportJobStatus.Failed, Assert.IsType<CancellationRequestResult.AlreadyTerminal>(result).View.Status);
        Assert.Equal(ReportJobStatus.Failed, (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Status);
    }

    [Fact]
    public async Task TwoConcurrentFirstCancellationsLetExactlyOneTransitionAndTheLoserIsAlreadyCancelling()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        // The SPEC-review interleaving case: two (here sixteen) concurrent first cancels. The
        // conditional UPDATE lets exactly one transition; every loser is classified
        // AlreadyCancelling and answers the current state (→202 at the endpoint), never a spurious
        // failure or NotFound. Exactly one intent exists — the single Transitioned — per the
        // ON CONFLICT DO NOTHING guard (D5).
        var attempts = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(
                () => Store.RequestCancellationAsync(acceptance.ReportJobId, Consumer, Now.AddSeconds(1), cancellationToken),
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results.OfType<CancellationRequestResult.Transitioned>());
        Assert.Equal(15, results.OfType<CancellationRequestResult.AlreadyCancelling>().Count());
        Assert.Empty(results.OfType<CancellationRequestResult.NotFound>());
        Assert.Empty(results.OfType<CancellationRequestResult.AlreadyTerminal>());
        foreach (var result in results)
        {
            var view = result switch
            {
                CancellationRequestResult.Transitioned transitioned => transitioned.View,
                CancellationRequestResult.AlreadyCancelling already => already.View,
                _ => throw new InvalidOperationException("a concurrent first cancel answered neither transition nor already-cancelling"),
            };
            Assert.Equal(ReportJobStatus.Cancelling, view.Status);
        }

        Assert.Equal(
            ReportJobStatus.Cancelling,
            (await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken))!.Status);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        const string Code = "ROW_BOUND_EXCEEDED";
        const string Reason =
            "the document cannot be used: code=ROW_BOUND_EXCEEDED reason=field-too-large size=262145 bound=262144 limit=row-bound ordinal=1500 offset=48213 path=$.description";

        Assert.Equal(OutputFailureOutcome.Applied, await Store.FailOutputAsync(acceptance.Outputs[0].OutputId, Code, Reason, Now, cancellationToken));
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

    /// <summary>
    /// An attempt's cause is written once: a second recording of the same output, block and
    /// attempt - even with another deadline class - writes nothing and answers that the row
    /// exists, so the first cause an attempt recorded stays the one a read returns.
    /// </summary>
    [Fact]
    public async Task AnAttemptCauseIsRecordedOnceAndARepeatChangesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);

        Assert.Equal(
            AttemptCauseOutcome.Recorded,
            await Store.RecordAttemptCauseAsync(
                new AttemptCause(acceptance.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.GuardDeadline, Now),
                cancellationToken));
        Assert.Equal(
            AttemptCauseOutcome.AlreadyRecorded,
            await Store.RecordAttemptCauseAsync(
                new AttemptCause(acceptance.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now.AddSeconds(1)),
                cancellationToken));

        var row = await Store.FindLastAttemptCauseAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken);
        Assert.NotNull(row);
        Assert.Equal(1, row!.Attempt);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, row.Cause);
        Assert.Equal(Now, row.RecordedAt);
    }

    /// <summary>
    /// "The last recorded cause" is the row with the greatest attempt: the read answers the
    /// third attempt after the three were recorded, an attempt that recorded nothing is no
    /// row at all, and a row of another output is never visible to this one's read.
    /// </summary>
    [Fact]
    public async Task TheLastCauseAcrossAttemptsIsTheOneReadAndAnotherOutputIsInvisible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var acceptance = await AcceptedAsync(cancellationToken);
        var other = await AcceptedAsync(cancellationToken);

        await Store.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now),
            cancellationToken);
        await Store.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 2, AttemptCauseVocabulary.WatchdogDeadline, Now.AddMinutes(1)),
            cancellationToken);
        await Store.RecordAttemptCauseAsync(
            new AttemptCause(acceptance.Outputs[0].OutputId, 0, 3, AttemptCauseVocabulary.GuardDeadline, Now.AddMinutes(2)),
            cancellationToken);

        var last = await Store.FindLastAttemptCauseAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken);
        Assert.NotNull(last);
        Assert.Equal(3, last!.Attempt);
        Assert.Equal(AttemptCauseVocabulary.GuardDeadline, last.Cause);
        Assert.Equal(Now.AddMinutes(2), last.RecordedAt);

        // The block no attempt recorded, and the other output's row, are not this read's.
        Assert.Null(await Store.FindLastAttemptCauseAsync(acceptance.Outputs[0].OutputId, 1, cancellationToken));
        Assert.Null(await Store.FindLastAttemptCauseAsync(other.Outputs[0].OutputId, 0, cancellationToken));

        await Store.RecordAttemptCauseAsync(
            new AttemptCause(other.Outputs[0].OutputId, 0, 1, AttemptCauseVocabulary.WatchdogDeadline, Now),
            cancellationToken);
        Assert.Equal(
            AttemptCauseVocabulary.GuardDeadline,
            (await Store.FindLastAttemptCauseAsync(acceptance.Outputs[0].OutputId, 0, cancellationToken))!.Cause);
        Assert.Equal(
            AttemptCauseVocabulary.WatchdogDeadline,
            (await Store.FindLastAttemptCauseAsync(other.Outputs[0].OutputId, 0, cancellationToken))!.Cause);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken);

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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        // The schema refuses a row that expires before it was created, and so does the double:
        // no store answers a publication that cannot describe a downloadable artifact.
        await Assert.ThrowsAnyAsync<Exception>(() => Store.PublishAsync(
            Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId) with { ExpiresAt = Now.AddSeconds(-1) },
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        var artifact = Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId);
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
        await Store.TransitionOutputAsync(otherOwned.Outputs[0].OutputId, Now, cancellationToken);
        var otherArtifact = ArtifactOf(otherOwned.ReportJobId, otherOwned.Outputs[0].OutputId, NextArtifactId(), OutputFormat.Pdf);
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
            await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
            var artifact = ArtifactOf(acceptance.ReportJobId, acceptance.Outputs[0].OutputId, NextArtifactId(), OutputFormat.Pdf);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        var artifact = ArtifactOf(acceptance.ReportJobId, acceptance.Outputs[0].OutputId, NextArtifactId(), OutputFormat.Pdf);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PublishOutcome.Published,
            (await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);

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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);

        Assert.Equal(PartCommitOutcome.Committed, (await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId), cancellationToken)).Outcome);
        var first = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(1, first!.BlocksCompleted);
        Assert.Equal(3, first.PagesGenerated);

        Assert.Equal(
            PartCommitOutcome.AlreadyCommitted,
            (await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId) with { PageCount = 9 }, cancellationToken)).Outcome);
        var repeated = await Store.FindStatusAsync(acceptance.ReportJobId, Consumer, cancellationToken);
        Assert.Equal(1, repeated!.BlocksCompleted);
        Assert.Equal(3, repeated.PagesGenerated);

        Assert.Equal(
            PartCommitOutcome.Committed,
            (await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId, blockIndex: 1) with { PageCount = 2 }, cancellationToken)).Outcome);
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
        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        await Store.PublishAsync(Artifact(acceptance.ReportJobId, acceptance.Outputs[0].OutputId), cancellationToken);
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

        await Store.TransitionOutputAsync(acceptance.Outputs[0].OutputId, Now, cancellationToken);
        Assert.Equal(
            PartCommitOutcome.Committed,
            (await Store.CommitPartAsync(Commit(acceptance.Outputs[0].OutputId) with { PageCount = 1 }, cancellationToken)).Outcome);
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
    public async Task APinCompletesThePresentationTheAcceptanceLeftOpen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        var written = await Store.PinTemplateAsync(
            acceptance.ReportJobId,
            PinOf(version),
            Now.AddSeconds(1),
            cancellationToken,
            new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" });

        Assert.True(written.WasWrittenByThisCall);

        // The job the render reads carries the presentation the version declared, and the
        // step's own read of the reference answers the same two values.
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("es-AR", state!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", state.Job.TimeZone);

        var reference = await Store.FindTemplateReferenceAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("es-AR", reference!.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", reference.TimeZone);

        // The value is completed once: a later attempt cannot give the job another one, and
        // the pin that was committed first still answers for the version.
        var second = await Store.PinTemplateAsync(
            acceptance.ReportJobId,
            PinOf(version, hash: new string('b', 64)),
            Now.AddSeconds(2),
            cancellationToken,
            new TemplateLocaleDefaults { Culture = "en-GB", TimeZone = "Europe/Madrid" });

        Assert.False(second.WasWrittenByThisCall);
        var afterSecondPin = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("es-AR", afterSecondPin!.Job.Culture);
        Assert.Equal("America/Argentina/Buenos_Aires", afterSecondPin.Job.TimeZone);
    }

    [Fact]
    public async Task APinKeepsThePresentationTheAcceptanceNamed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken)) with
        {
            Culture = "en-us",
            TimeZone = "Asia/Tokyo",
        };
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        await Store.PinTemplateAsync(
            acceptance.ReportJobId,
            PinOf(version),
            Now.AddSeconds(1),
            cancellationToken,
            new TemplateLocaleDefaults { Culture = "es-AR", TimeZone = "America/Argentina/Buenos_Aires" });

        // What the request named is the job's, in its own spelling: a package of another
        // culture can never move a report that was accepted in this one.
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Equal("en-us", state!.Job.Culture);
        Assert.Equal("Asia/Tokyo", state.Job.TimeZone);
    }

    [Fact]
    public async Task APinOfAPackageThatDeclaresNoPresentationLeavesTheJobWithoutOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = await TakenAsync("1.0.0", cancellationToken);
        var acceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(acceptance, Now, cancellationToken);

        await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken, presentation: null);

        // A package that declares none completes nothing: the render refuses the job, which
        // is the stop the approved plan names, never a guess.
        var state = await Store.FindForWorkflowAsync(acceptance.ReportJobId, cancellationToken);
        Assert.Null(state!.Job.Culture);
        Assert.Null(state.Job.TimeZone);
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

        var first = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken, presentation: null);
        var second = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version, hash: new string('b', 64)), Now.AddSeconds(2), cancellationToken, presentation: null);

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
        var gitFirst = await Store.PinTemplateAsync(gitAcceptance.ReportJobId, PinOfGit(version), Now.AddSeconds(1), cancellationToken, presentation: null);
        var storageSecond = await Store.PinTemplateAsync(gitAcceptance.ReportJobId, PinOf(version), Now.AddSeconds(2), cancellationToken, presentation: null);

        Assert.True(gitFirst.WasWrittenByThisCall);
        Assert.False(storageSecond.WasWrittenByThisCall);
        Assert.Equal(gitFirst.Pin!.CommitSha, storageSecond.Pin!.CommitSha);
        Assert.Equal(gitFirst.Pin.PackageSha256, storageSecond.Pin.PackageSha256);
        Assert.Null(storageSecond.Pin.SourceDescriptorSha256);
        Assert.Null(storageSecond.Pin.SourceObjects);

        // And the reverse: a storage-shaped pin first, a legacy one after it.
        var storageAcceptance = Acceptance(await SeedAsync(UploadState.Ready, cancellationToken: cancellationToken));
        await Store.AcceptAsync(storageAcceptance, Now, cancellationToken);
        var storageFirst = await Store.PinTemplateAsync(storageAcceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken, presentation: null);
        var gitSecond = await Store.PinTemplateAsync(storageAcceptance.ReportJobId, PinOfGit(version), Now.AddSeconds(2), cancellationToken, presentation: null);

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

        var state = await Store.PinTemplateAsync(acceptance.ReportJobId, PinOf(version), Now.AddSeconds(1), cancellationToken, presentation: null);

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

    /// <summary>
    /// Seeds an additional output of a job in the given state, for the ADR-048 rollup cases
    /// (SPEC-261125-1224 Phase D.6). A store that cannot arrange one answers <see langword="false"/>
    /// and the case asserts nothing it cannot present.
    /// </summary>
    /// <param name="reportJobId">The job the output belongs to.</param>
    /// <param name="outputId">The identifier of the new output.</param>
    /// <param name="format">The format the output produces.</param>
    /// <param name="status">The state to leave the output in.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected virtual Task<bool> TrySeedOutputAsync(
        string reportJobId,
        string outputId,
        OutputFormat format,
        OutputStatus status,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>
    /// Cancels an existing output of a job in place, for the all-cancelled rollup case
    /// (ADR-048 R4, SPEC-261125-1224 Phase D.6). A store that cannot answers <see langword="false"/>.
    /// </summary>
    /// <param name="outputId">The identifier of the existing output.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected virtual Task<bool> TryCancelExistingOutputAsync(string outputId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>
    /// Puts a job into the non-terminal <c>Cancelling</c> state, for the R0 override case
    /// (ADR-048, SPEC-261125-1224 Phase D.6). A store that cannot answers <see langword="false"/>.
    /// </summary>
    /// <param name="reportJobId">The job to put into cancellation.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected virtual Task<bool> TrySeedCancellingJobAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>
    /// Removes every output of a job, so a case can present a job that requests none — the shape a
    /// cancelled job with no requested output closes by R0 without the wait guard. A store that
    /// cannot arrange one answers <see langword="false"/>.
    /// </summary>
    /// <param name="reportJobId">The job whose outputs are removed.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    protected virtual Task<bool> TryRemoveOutputsAsync(string reportJobId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    /// <summary>An acceptance of a job over <paramref name="uploadId"/>, with identifiers of their own.</summary>
    /// <param name="uploadId">The upload the job reports on.</param>
    /// <param name="consumerId">The consumer the job is accepted for.</param>
    /// <param name="version">The version the acceptance names; null for a reference by name.</param>
    /// <param name="fingerprint">The identity of the request; the one of this acceptance when null.</param>
    /// <param name="idempotencyKey">The key the request carried; null when it carried none.</param>
    protected JobAcceptance Acceptance(
        string uploadId,
        string consumerId = Consumer,
        string? version = "1.0.0",
        RequestFingerprint? fingerprint = null,
        string? idempotencyKey = null)
    {
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            consumerId,
            uploadId,
            "pilot-grouped",
            version is null ? TemplateReferenceForm.ByName : TemplateReferenceForm.Exact,
            version,
            fingerprint ?? RequestIdentity(consumerId, uploadId, version));
        return idempotencyKey is null ? acceptance : acceptance with { IdempotencyKey = idempotencyKey };
    }

    /// <summary>
    /// The identity of one request as this contract computes it: a deterministic value over the
    /// consumer, the upload and the version, so the same request has the same identity and a
    /// different one has a different identity. It is deliberately not the product's encoder —
    /// the contract exercises the store, and the encoder has its own cases.
    /// </summary>
    /// <param name="consumerId">The consumer the request was sent by.</param>
    /// <param name="uploadId">The upload the request names.</param>
    /// <param name="version">The version the request names; null for a reference by name.</param>
    protected static RequestFingerprint RequestIdentity(string consumerId, string uploadId, string? version) =>
        RequestFingerprint.FromDigest(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    "request-identity/v1\n" + consumerId + "\n" + uploadId + "\n" + (version ?? "(none)"))));

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
