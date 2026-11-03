using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The two steps that record and clear a job's waiting stage, run the way the engine
/// runs them but without one: each answers with the outcome the store gave and never
/// with a failure, because a job that ended meanwhile and a step that arrives twice are
/// both ordinary answers of a conditional write.
/// </summary>
public sealed class InputWaitActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordingTheWaitOfAQueuedJobAppliesTheStage()
    {
        using var rig = await Rig.CreateAsync();

        var outcome = await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));

        Assert.Equal(BeginInputWaitOutcome.Applied, outcome);
        Assert.Equal(InputWaitStage.WaitingForInput, await rig.StageAsync());
        Assert.Equal(ReportJobStatus.Queued, await rig.StatusAsync());
    }

    [Fact]
    public async Task RecordingTheWaitTwiceChangesNothing()
    {
        using var rig = await Rig.CreateAsync();
        await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));

        var again = await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));

        Assert.Equal(BeginInputWaitOutcome.Applied, again);
        Assert.Equal(InputWaitStage.WaitingForInput, await rig.StageAsync());
    }

    [Fact]
    public async Task RecordingTheWaitOfAJobThatAlreadyEndedIsAnAnswerAndNotAFailure()
    {
        using var rig = await Rig.CreateAsync();
        Assert.Equal(
            JobFailureOutcome.Applied,
            await rig.Jobs.FailJobAsync(rig.ReportJobId, "INPUT_NOT_FOUND", "input-expired", Now, TestContext.Current.CancellationToken));

        var outcome = await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));

        Assert.Equal(BeginInputWaitOutcome.NotQueued, outcome);
        Assert.Null(await rig.StageAsync());
        Assert.Equal(ReportJobStatus.Failed, await rig.StatusAsync());
    }

    [Fact]
    public async Task RecordingTheWaitOfAJobThatDoesNotExistIsAnAnswerAndNotAFailure()
    {
        using var rig = await Rig.CreateAsync();

        var outcome = await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput("job-ffffffffffffffffffffffffffffffff")));

        Assert.Equal(BeginInputWaitOutcome.NotQueued, outcome);
    }

    [Fact]
    public async Task ClearingTheWaitOfAWaitingJobAppliesTheChange()
    {
        using var rig = await Rig.CreateAsync();
        await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));

        var outcome = await rig.RunAsync(activities => activities.EndAsync(new InputWaitInput(rig.ReportJobId)));

        Assert.Equal(EndInputWaitOutcome.Applied, outcome);
        Assert.Null(await rig.StageAsync());
    }

    [Fact]
    public async Task ClearingTheWaitTwiceIsANoOp()
    {
        using var rig = await Rig.CreateAsync();
        await rig.RunAsync(activities => activities.BeginAsync(new InputWaitInput(rig.ReportJobId)));
        await rig.RunAsync(activities => activities.EndAsync(new InputWaitInput(rig.ReportJobId)));

        var again = await rig.RunAsync(activities => activities.EndAsync(new InputWaitInput(rig.ReportJobId)));

        Assert.Equal(EndInputWaitOutcome.NotWaiting, again);
        Assert.Null(await rig.StageAsync());
    }

    /// <summary>A job accepted on a confirming upload, and the two steps over its records.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly InMemoryUploadStore _uploads;
        private readonly ActivityEnvironment _environment = new();

        private Rig(InMemoryUploadStore uploads, InMemoryJobStore jobs, string reportJobId)
        {
            _uploads = uploads;
            Jobs = jobs;
            ReportJobId = reportJobId;
            Activities = new InputWaitActivities(jobs, NullLoggerFactory.Instance, NullLogger<InputWaitActivities>.Instance);
        }

        public InMemoryJobStore Jobs { get; }

        public InputWaitActivities Activities { get; }

        public string ReportJobId { get; }

        public static async Task<Rig> CreateAsync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var uploads = new InMemoryUploadStore();
            var jobs = new InMemoryJobStore(uploads);
            const string Consumer = "dev-consumer";
            const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";
            var uploadId = UploadIdentifier.Prefix + "0000000000000000000000000000000a";

            await uploads.CreateAsync(
                new UploadRecord(
                    uploadId,
                    Consumer,
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    ObjectKey.FromSegments("staging", uploadId, "content"),
                    Now.AddHours(-1),
                    Now.AddHours(1)),
                cancellationToken);
            await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, Now, cancellationToken);
            await uploads.RecordTransferAsync(uploadId, UploadContentOutcome.Complete, 900, 800, Hash, Now, cancellationToken);
            await uploads.ConfirmAsync(uploadId, Consumer, Hash, Now, Now, Now.AddHours(6), cancellationToken);

            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                uploadId,
                "pilot-grouped",
                TemplateReferenceForm.Exact,
                "1.0.0");
            Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, Now, cancellationToken));

            return new Rig(uploads, jobs, acceptance.ReportJobId);
        }

        public Task<T> RunAsync<T>(Func<InputWaitActivities, Task<T>> activity) => _environment.RunAsync(() => activity(Activities));

        public async Task<string?> StageAsync() =>
            (await Jobs.FindForWorkflowAsync(ReportJobId, TestContext.Current.CancellationToken))?.Job.Stage;

        public async Task<ReportJobStatus?> StatusAsync() =>
            (await Jobs.FindForWorkflowAsync(ReportJobId, TestContext.Current.CancellationToken))?.Job.Status;

        public void Dispose() => _ = _uploads;
    }
}
