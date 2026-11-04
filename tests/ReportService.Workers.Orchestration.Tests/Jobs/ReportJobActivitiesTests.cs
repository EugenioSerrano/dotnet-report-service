using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using ReportService.Workers.Orchestration.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Testing;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The two steps a job runs, exercised in the engine's own host for one attempt: the
/// input check answers what each state of the upload and of the job means — ready and
/// verifying leave the job where it is, a rejection answers with the upload's own
/// code, an upload that ran out of time is expired even before anything marks it, and
/// a job that had already ended is terminal — and the failure ends the job and its
/// output once, whatever a later attempt asks for. The job is read through the shared
/// record store, so the steps are asserted against the records the service keeps and
/// not against a story about them.
/// </summary>
public sealed class ReportJobActivitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private const string Consumer = "dev-consumer";

    [Fact]
    public async Task AReadyInputLeavesTheJobQueuedAndStartsNoFailure()
    {
        using var rig = await Rig.CreateAsync();

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Ready, resolution.Outcome);
        Assert.Null(resolution.RejectionCode);
        Assert.Equal(0, rig.Jobs.FailCalls);
        var state = await rig.StateAsync();
        Assert.Equal(ReportJobStatus.Queued, state.Job.Status);
        Assert.Null(state.Job.FinishedAt);
        Assert.Equal(OutputStatus.Pending, Assert.Single(state.Outputs).Status);
    }

    [Fact]
    public async Task AVerifyingInputInTimeStopsAtTheContinuationPoint()
    {
        using var rig = await Rig.CreateAsync(UploadState.Verifying);

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Verifying, resolution.Outcome);
        Assert.Equal(0, rig.Jobs.FailCalls);
        var state = await rig.StateAsync();
        Assert.Equal(ReportJobStatus.Queued, state.Job.Status);
        Assert.Equal(UploadStatus.Verifying, state.UploadStatus);
    }

    [Fact]
    public async Task AVerifyingInputPastItsDeadlineIsExpiredEvenBeforeAnythingMarksIt()
    {
        using var rig = await Rig.CreateAsync(UploadState.Verifying);
        rig.Clock.Advance(TimeSpan.FromHours(7));

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Expired, resolution.Outcome);
        Assert.Equal(UploadStatus.Verifying, (await rig.StateAsync()).UploadStatus);
    }

    [Fact]
    public async Task ARejectedInputAnswersWithTheUploadsOwnCode()
    {
        using var rig = await Rig.CreateAsync(UploadState.Rejected);

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Rejected, resolution.Outcome);
        Assert.Equal(UploadRejectionCode.DataInvalid, resolution.RejectedWith());
    }

    [Fact]
    public async Task AnInputThatIsNotRejectedHasNoRejectionCode()
    {
        using var rig = await Rig.CreateAsync();

        var resolution = await rig.ResolveAsync();

        Assert.Throws<InvalidOperationException>(() => resolution.RejectedWith());
    }

    [Fact]
    public async Task AnExpiredSessionIsExpired()
    {
        using var rig = await Rig.CreateAsync(UploadState.Expired);

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Expired, resolution.Outcome);
    }

    [Fact]
    public async Task TheOutcomeOfAWaitIsCountedWithAConstantOutcome()
    {
        using var rig = await Rig.CreateAsync();
        using var measurements = new JobOutcomeCapture(rig.ReportJobId);
        await rig.Jobs.BeginInputWaitAsync(rig.ReportJobId, TestContext.Current.CancellationToken);

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Ready, resolution.Outcome);
        var counted = Assert.Single(
            measurements.Captured,
            measurement => measurement.Name == OrchestrationMetrics.InputWaitOutcomesInstrumentName);
        Assert.Equal(1L, counted.Value);
        Assert.Equal("ready", counted.Tag("outcome"));
    }

    [Fact]
    public async Task ACheckThatIsNotPartOfAWaitIsNotCountedAsOne()
    {
        using var rig = await Rig.CreateAsync();
        using var measurements = new JobOutcomeCapture(rig.ReportJobId);

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Ready, resolution.Outcome);
        Assert.DoesNotContain(
            measurements.Captured,
            measurement => measurement.Name == OrchestrationMetrics.InputWaitOutcomesInstrumentName);
    }

    [Fact]
    public async Task AJobThatHadAlreadyEndedIsTerminal()
    {
        using var rig = await Rig.CreateAsync();
        await rig.FailAsync("INPUT_NOT_FOUND", "input-expired");

        var resolution = await rig.ResolveAsync();

        Assert.Equal(InputOutcome.Terminal, resolution.Outcome);
    }

    [Fact]
    public async Task AJobThatIsNotRecordedIsTerminal()
    {
        using var rig = await Rig.CreateAsync();

        var resolution = await rig.ResolveAsync("job-ffffffffffffffffffffffffffffffff");

        Assert.Equal(InputOutcome.Terminal, resolution.Outcome);
    }

    [Fact]
    public async Task FailingAJobEndsTheOutputAndTheJobOnce()
    {
        using var rig = await Rig.CreateAsync();

        await rig.FailAsync("INPUT_NOT_FOUND", "input-expired");

        var state = await rig.StateAsync();
        Assert.Equal(ReportJobStatus.Failed, state.Job.Status);
        Assert.Equal("INPUT_NOT_FOUND", state.Job.ErrorCode);
        Assert.Equal("input-expired", state.Job.ErrorReason);
        Assert.Equal(Now, state.Job.FinishedAt);
        Assert.True(state.Job.IsTerminal);
        var output = Assert.Single(state.Outputs);
        Assert.Equal(OutputStatus.Failed, output.Status);
        Assert.Equal("INPUT_NOT_FOUND", output.ErrorCode);
        Assert.Equal(Now, output.FinishedAt);

        // A repeated attempt — a retry, or a second step that arrived late — changes nothing,
        // and the job keeps the condition it first ended with.
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        await rig.FailAsync("TEMPLATE_NOT_FOUND", "template-missing");

        var after = await rig.StateAsync();
        Assert.Equal("INPUT_NOT_FOUND", after.Job.ErrorCode);
        Assert.Equal("input-expired", after.Job.ErrorReason);
        Assert.Equal(Now, after.Job.FinishedAt);
        Assert.Equal("INPUT_NOT_FOUND", Assert.Single(after.Outputs).ErrorCode);
    }

    [Fact]
    public async Task TheJobOutcomeIsCountedWithItsStatusAndItsCode()
    {
        using var rig = await Rig.CreateAsync();
        using var measurements = new JobOutcomeCapture(rig.ReportJobId);

        await rig.FailAsync("INPUT_NOT_FOUND", "input-expired");

        var counted = Assert.Single(measurements.Captured, measurement => measurement.Name == OrchestrationMetrics.JobOutcomesInstrumentName);
        Assert.Equal(1L, counted.Value);
        Assert.Equal("FAILED", counted.Tag("status"));
        Assert.Equal("INPUT_NOT_FOUND", counted.Tag("code"));
    }

    /// <summary>The states a job's upload can be in when the job's input is checked.</summary>
    private enum UploadState
    {
        Ready,
        Verifying,
        Rejected,
        Expired,
    }

    /// <summary>
    /// One job over one upload: the two record stores the steps read and write, the
    /// clock the test moves, and the engine's host around the activity that is run.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        private readonly OrchestrationMetrics _metrics = new(MeterIdentity.ForHost(TelemetryDefaults.MeterName));

        private Rig()
        {
            Clock = new ManualTimeProvider(Now);
            Uploads = new InMemoryUploadStore();
            Jobs = new InMemoryJobStore(Uploads);
            Activities = new ReportJobActivities(Jobs, Clock, _metrics, NullLoggerFactory.Instance, NullLogger<ReportJobActivities>.Instance);
        }

        public ManualTimeProvider Clock { get; }

        public InMemoryUploadStore Uploads { get; }

        public InMemoryJobStore Jobs { get; }

        public ReportJobActivities Activities { get; }

        public string ReportJobId { get; private set; } = string.Empty;

        public string UploadId { get; private set; } = string.Empty;

        public static async Task<Rig> CreateAsync(UploadState state = UploadState.Ready)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var rig = new Rig();
            rig.UploadId = "upload-0123456789abcdef0123456789abcdef";
            var stagingKey = ObjectKey.FromSegments("staging", rig.UploadId, "content");
            var record = new UploadRecord(
                rig.UploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                stagingKey,
                Now.AddHours(-1),
                Now.AddHours(1));
            await rig.Uploads.CreateAsync(record, cancellationToken);
            await rig.Uploads.TryStartTransferAsync(rig.UploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken);
            await rig.Uploads.RecordTransferAsync(
                rig.UploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), Now.AddMinutes(-30), cancellationToken);

            // The job is accepted while its upload is still confirming and in time — the state
            // the acceptance admits — and the upload then ends the way the case asks.
            await rig.Uploads.ConfirmAsync(
                rig.UploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken);

            if (state is UploadState.Ready)
            {
                await rig.Uploads.CommitReadyAsync(
                    new SnapshotRecord(
                        rig.UploadId,
                        Consumer,
                        ObjectKey.FromSegments("snapshots", rig.UploadId, "a"),
                        UploadContentEncoding.None,
                        DataProfile.HeaderItemsV1,
                        new string('a', 64),
                        900,
                        new string('a', 64),
                        900,
                        Now.AddMinutes(-20)),
                    Now.AddMinutes(-20),
                    cancellationToken);
            }

            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                rig.UploadId,
                "pilot-grouped",
                TemplateReferenceForm.Exact,
                "1.0.0");
            var accepted = await rig.Jobs.AcceptAsync(acceptance, Now, cancellationToken);
            Assert.IsType<JobAcceptanceResult.Accepted>(accepted);
            rig.ReportJobId = acceptance.ReportJobId;

            if (state is UploadState.Rejected)
            {
                await rig.Uploads.CommitRejectedAsync(
                    rig.UploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Now.AddMinutes(-10), cancellationToken);
            }
            else if (state is UploadState.Expired)
            {
                await rig.Uploads.ExpireIfOverdueAsync(rig.UploadId, Now.AddHours(6), cancellationToken);
            }

            return rig;
        }

        /// <summary>Runs the input check of the job inside the engine's host.</summary>
        public Task<ResolveInputResult> ResolveAsync() => ResolveAsync(ReportJobId);

        /// <summary>Runs the input check of <paramref name="reportJobId"/> inside the engine's host.</summary>
        public Task<ResolveInputResult> ResolveAsync(string reportJobId) =>
            RunAsync(() => Activities.ResolveInputAsync(new ResolveInputInput(reportJobId)), reportJobId);

        /// <summary>Runs the failure of the job inside the engine's host.</summary>
        public Task FailAsync(string code, string reason) =>
            RunAsync(() => Activities.FailJobAsync(new FailJobInput(ReportJobId, code, reason)));

        /// <summary>The job and its outputs as the service now keeps them.</summary>
        public async Task<JobStateRecord> StateAsync()
        {
            var state = await Jobs.FindForWorkflowAsync(ReportJobId, TestContext.Current.CancellationToken);
            Assert.NotNull(state);
            return state!;
        }

        public void Dispose() => _metrics.Dispose();

        private Task<T> RunAsync<T>(Func<Task<T>> activity, string? reportJobId = null)
        {
            var environment = new ActivityEnvironment
            {
                Info = Info(reportJobId ?? ReportJobId),
            };
            return environment.RunAsync(activity);
        }

        private Task RunAsync(Func<Task> activity)
        {
            var environment = new ActivityEnvironment
            {
                Info = Info(ReportJobId),
            };
            return environment.RunAsync(activity);
        }

        private static Temporalio.Activities.ActivityInfo Info(string reportJobId) =>
            ActivityEnvironment.DefaultInfo with
            {
                WorkflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId),
                WorkflowRunId = "run-0123456789abcdef",
                WorkflowType = ReportJobWorkflow.WorkflowType,
                ActivityId = "1",
                TaskQueue = ReportJobActivityOptions.Queue,
                Attempt = 1,
            };
    }
}
