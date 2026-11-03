extern alias orchestration;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using InputWaitActivities = orchestration::ReportService.Workers.Orchestration.Jobs.InputWaitActivities;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Records the job histories the hermetic suite replays, once, so that suite has something
/// to check. It is opt-in: it runs only when the environment variable names the directory
/// to write, so the integration command never runs it, and the ordinary cases of this suite
/// are untouched by it. It needs one engine and nothing else — the jobs it runs live in the
/// in-memory record stores — so every history it writes is the outcome of a real execution
/// of the workflow and its steps, taken under the very settings the case that replays it
/// replays it under.
/// </summary>
public sealed class ReportJobHistoryCapture
{
    /// <summary>The variable that names the directory the captured histories are written to; without it the case is skipped.</summary>
    public const string OutputVariable = "REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY";

    /// <summary>How long one recorded execution, or the wait for it to reach a point, may take.</summary>
    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromSeconds(60);

    /// <summary>The instant the records of the first recording are written with.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    /// <summary>The consumer every recorded job is accepted for.</summary>
    private const string Consumer = "dev-consumer";

    /// <summary>The states a recorded job's upload is in when its execution starts.</summary>
    private enum UploadState
    {
        Ready,
        Rejected,
        Verifying,
    }

    /// <summary>What ends the wait of a recorded job while it waits.</summary>
    private enum UploadEnding
    {
        Ready,
        Rejected,
        Expired,
    }

    /// <summary>
    /// Records every history the hermetic suite replays: a job whose first check of its
    /// upload already answered, a job whose own timer ends its wait, the two ways a
    /// notification ends it, the upload that outlived its verification deadline, and the
    /// two runs of a continuation.
    /// </summary>
    [Fact]
    public async Task EveryReplayedJobHistoryIsRecorded()
    {
        var target = Target();
        await using var rig = await Rig.StartAsync();

        await RecordTheFirstCheckAsync(rig, JobHistoryFixtures.Ready, UploadState.Ready, target);
        await RecordTheFirstCheckAsync(rig, JobHistoryFixtures.Rejected, UploadState.Rejected, target);

        await RecordTheTimerEndingTheWaitAsync(rig, JobHistoryFixtures.Verifying, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenReadyAfterANotification, UploadEnding.Ready, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenRejected, UploadEnding.Rejected, target);
        await RecordTheNotificationEndingTheWaitAsync(
            rig, JobHistoryFixtures.WaitingThenExpired, UploadEnding.Expired, target);

        await RecordTheRunThatContinuedAsync(rig, JobHistoryFixtures.ContinuedAsNew, target);
        await RecordTheRunAfterTheContinuationAsync(rig, JobHistoryFixtures.ContinuedAfterANotification, target);
    }

    private static string Target()
    {
        var target = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip($"the capture runs once, when {OutputVariable} names the directory to write.");
        }

        Directory.CreateDirectory(target!);
        return target!;
    }

    private static async Task RecordTheFirstCheckAsync(
        Rig rig,
        JobHistoryFixture fixture,
        UploadState state,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, state, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheTimerEndingTheWaitAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, runId, fixture, cancellationToken);

        // The upload becomes usable only after the job has checked it once and gone to
        // sleep, so the check that follows is the one the job's own timer wakes. Nothing
        // notifies the job: this recording is the guarantee that no notification is needed.
        await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheNotificationEndingTheWaitAsync(
        Rig rig,
        JobHistoryFixture fixture,
        UploadEnding ending,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, runId, fixture, cancellationToken);

        switch (ending)
        {
            case UploadEnding.Ready:
                await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
                break;

            case UploadEnding.Rejected:
                await rig.MakeRejectedAsync(rig.UploadOf(reportJobId), cancellationToken);
                break;

            case UploadEnding.Expired:
                // Nothing marks the upload expired: the job's own check answers that its
                // verification deadline has passed, which is the rule the product applies.
                rig.AgePastTheVerificationDeadline();
                break;

            default:
                throw new InvalidOperationException("the capture does not know this ending.");
        }

        await rig.NotifyAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);
    }

    private static async Task RecordTheRunThatContinuedAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);

        await rig.WaitForTheContinuationAsync(reportJobId, runId, fixture, cancellationToken);
        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, runId, cancellationToken), cancellationToken);

        // The run that followed keeps waiting for an upload that never settles; it is not
        // part of this recording, so the capture ends the execution before the next one.
        await rig.Client.GetWorkflowHandle<ReportJobWorkflow>(Rig.WorkflowIdOf(reportJobId))
            .TerminateAsync("the run that continued was recorded and the run after it is not part of it");
    }

    private static async Task RecordTheRunAfterTheContinuationAsync(
        Rig rig,
        JobHistoryFixture fixture,
        string directory)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var reportJobId = await rig.GivenJobAsync(fixture, UploadState.Verifying, cancellationToken);
        var (handle, runId) = await rig.StartJobAsync(reportJobId, cancellationToken);

        var nextRunId = await rig.WaitForTheNextRunAsync(reportJobId, runId, fixture, cancellationToken);
        await rig.WaitForItsWaitAsync(reportJobId, nextRunId, fixture, cancellationToken);

        // The notification arrives while the run that followed the continuation is already
        // waiting, and the check it wakes is the one that ends the job with its upload.
        await rig.MakeReadyAsync(rig.UploadOf(reportJobId), cancellationToken);
        await rig.NotifyAsync(reportJobId, cancellationToken);
        await rig.AwaitTheEndAsync(handle, reportJobId, cancellationToken);

        await WriteAsync(directory, fixture, await rig.HistoryAsync(reportJobId, nextRunId, cancellationToken), cancellationToken);
    }

    private static async Task WriteAsync(
        string directory,
        JobHistoryFixture fixture,
        WorkflowHistory history,
        CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(Path.Combine(directory, fixture.File), history.ToJson(), cancellationToken);

    /// <summary>
    /// One engine, one worker on its queue, one clock, and the records every recording runs
    /// over. The engine allows one worker per queue, so the recordings share it and differ
    /// in the settings the workflow publishes before each execution starts.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly WorkflowEnvironment _environment;
        private readonly TemporalWorker _worker;
        private readonly OrchestrationMetrics _metrics = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _running;
        private readonly Dictionary<string, string> _uploadOf = new(StringComparer.Ordinal);
        private int _sequence;

        private Rig(
            WorkflowEnvironment environment,
            TemporalWorker worker,
            OrchestrationMetrics metrics,
            Task running,
            InMemoryUploadStore uploads,
            InMemoryJobStore jobs,
            ManualTimeProvider clock)
        {
            _environment = environment;
            _worker = worker;
            _metrics = metrics;
            _running = running;
            Uploads = uploads;
            Jobs = jobs;
            Clock = clock;
        }

        public ITemporalClient Client => _environment.Client;

        public InMemoryUploadStore Uploads { get; }

        public InMemoryJobStore Jobs { get; }

        /// <summary>
        /// The instant the recording being taken writes its records with. It never follows
        /// the wall clock, and the one recording that moves it forward seeds the records it
        /// needs from where it has got to, so no recording inherits another one's instant.
        /// </summary>
        public ManualTimeProvider Clock { get; }

        public static string WorkflowIdOf(string reportJobId) => ReportJobWorkflow.WorkflowIdFor(reportJobId);

        public static async Task<Rig> StartAsync()
        {
            var uploads = new InMemoryUploadStore();
            var jobs = new InMemoryJobStore(uploads);
            var clock = new ManualTimeProvider(Now);
            var metrics = new OrchestrationMetrics();

            // The workflow's own bounds are published per recording, before each execution
            // starts; the worker serves every recording of this rig in turn.
            var environment = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions());
            var worker = new TemporalWorker(
                environment.Client,
                new TemporalWorkerOptions(ReportJobWorkflow.OrchestrationQueue)
                    .AddWorkflow<ReportJobWorkflow>()
                    .AddAllActivities(new ReportJobActivities(
                        jobs,
                        clock,
                        metrics,
                        NullLogger<ReportJobActivities>.Instance))
                    .AddAllActivities(new InputWaitActivities(jobs, NullLogger<InputWaitActivities>.Instance)));

            var stop = new CancellationTokenSource();
            var running = worker.ExecuteAsync(stop.Token);
            return new Rig(environment, worker, metrics, running, uploads, jobs, clock);
        }

        public string UploadOf(string reportJobId) => _uploadOf[reportJobId];

        public WorkflowHandle<ReportJobWorkflow> Handle(string reportJobId, string runId) =>
            Client.GetWorkflowHandle<ReportJobWorkflow>(WorkflowIdOf(reportJobId), runId: runId);

        public async Task<string> GivenJobAsync(
            JobHistoryFixture fixture,
            UploadState state,
            CancellationToken cancellationToken)
        {
            ReportJobWorkflow.Configure(fixture.Backstop, fixture.ContinueAsNewAfter);
            var now = Clock.GetUtcNow();

            var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", CultureInfo.InvariantCulture);
            var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
            await Uploads.CreateAsync(
                new UploadRecord(
                    uploadId,
                    Consumer,
                    UploadMode.ApiStream,
                    DataProfile.HeaderItemsV1,
                    UploadStatus.Created,
                    stagingKey,
                    now.AddHours(-1),
                    now.AddHours(1)),
                cancellationToken);
            await Uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now.AddMinutes(-30), cancellationToken);
            await Uploads.RecordTransferAsync(
                uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now.AddMinutes(-30), cancellationToken);
            await Uploads.ConfirmAsync(
                uploadId, Consumer, null, now.AddMinutes(-30), now.AddMinutes(-30), now.AddHours(5), cancellationToken);

            if (state is UploadState.Ready)
            {
                await MakeReadyAsync(uploadId, cancellationToken);
            }

            var acceptance = new JobAcceptance(
                JobIdentifier.Create(),
                JobIdentifier.CreateOutput(),
                Consumer,
                uploadId,
                "pilot-grouped",
                TemplateReferenceForm.Exact,
                "1.0.0");
            if (await Jobs.AcceptAsync(acceptance, now, cancellationToken) is not JobAcceptanceResult.Accepted)
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"the capture's job over {uploadId} had to be accepted"));
            }

            if (state is UploadState.Rejected)
            {
                await MakeRejectedAsync(uploadId, cancellationToken);
            }

            _uploadOf[acceptance.ReportJobId] = uploadId;
            return acceptance.ReportJobId;
        }

        public async Task<(WorkflowHandle<ReportJobWorkflow> Handle, string RunId)> StartJobAsync(
            string reportJobId,
            CancellationToken cancellationToken)
        {
            var handle = await Client.StartWorkflowAsync(
                (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
                new WorkflowOptions
                {
                    Id = WorkflowIdOf(reportJobId),
                    TaskQueue = ReportJobWorkflow.OrchestrationQueue,
                });

            // The run this execution started is read back from the engine rather than from
            // the handle: a handle addresses a workflow, and only the engine says which of
            // its runs is current once a run continues as a new one.
            var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                Describe(handle.Id),
                new RpcOptions { CancellationToken = cancellationToken });
            return (handle, described.WorkflowExecutionInfo.Execution.RunId);
        }

        public async Task AwaitTheEndAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            string reportJobId,
            CancellationToken cancellationToken)
        {
            try
            {
                await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                throw await DescribeTheUnfinishedRunAsync(handle, reportJobId, exception, cancellationToken);
            }
        }

        public async Task WaitForItsWaitAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken) =>
            await Wait.UntilAsync(
                $"{fixture.File}: the run to check the upload once and wait for it",
                async token => EventsOf(await FetchAsync(Handle(reportJobId, runId), token))
                    .Any(entry => entry.EventType == EventType.TimerStarted),
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);

        public async Task WaitForTheContinuationAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken) =>
            await Wait.UntilAsync(
                $"{fixture.File}: the run to take every check it may and continue as a new one",
                async token => EventsOf(await FetchAsync(Handle(reportJobId, runId), token))
                    .Any(entry => entry.EventType == EventType.WorkflowExecutionContinuedAsNew),
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);

        public async Task<string> WaitForTheNextRunAsync(
            string reportJobId,
            string runId,
            JobHistoryFixture fixture,
            CancellationToken cancellationToken)
        {
            string? next = null;
            await Wait.UntilAsync(
                $"{fixture.File}: the run to continue as a new one",
                async token =>
                {
                    var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                        Describe(WorkflowIdOf(reportJobId)),
                        new RpcOptions { CancellationToken = token });
                    next = described.WorkflowExecutionInfo.Execution.RunId;
                    return !string.Equals(next, runId, StringComparison.Ordinal);
                },
                ExecutionDeadline,
                TimeSpan.FromMilliseconds(50),
                cancellationToken);
            return next!;
        }

        public Task<WorkflowHistory> HistoryAsync(
            string reportJobId,
            string runId,
            CancellationToken cancellationToken) => FetchAsync(Handle(reportJobId, runId), cancellationToken);

        public Task NotifyAsync(string reportJobId, CancellationToken cancellationToken) =>
            Client.GetWorkflowHandle<ReportJobWorkflow>(WorkflowIdOf(reportJobId))
                .SignalAsync((ReportJobWorkflow workflow) => workflow.OnInputChangedAsync());

        public async Task MakeReadyAsync(string uploadId, CancellationToken cancellationToken) =>
            await Uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    new string('a', 64),
                    900,
                    new string('a', 64),
                    900,
                    Clock.GetUtcNow().AddMinutes(-20)),
                Clock.GetUtcNow().AddMinutes(-20),
                cancellationToken);

        public async Task MakeRejectedAsync(string uploadId, CancellationToken cancellationToken) =>
            await Uploads.CommitRejectedAsync(
                uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Clock.GetUtcNow(), cancellationToken);

        /// <summary>Moves the clock past the verification deadline of the upload being recorded.</summary>
        public void AgePastTheVerificationDeadline() => Clock.Advance(TimeSpan.FromHours(6));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();

            // A worker is stopped by being disposed. This version's run task does not report
            // that it has finished, so it is given a moment and then left to the process
            // rather than holding the capture open for a task that never completes.
            _worker.Dispose();
            try
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
                // The worker is stopped; only its task's report is missing.
            }

            _stop.Dispose();
            await _environment.DisposeAsync();
            _metrics.Dispose();
        }

        private static DescribeWorkflowExecutionRequest Describe(string workflowId) => new()
        {
            Namespace = "default",
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution
            {
                WorkflowId = workflowId,
            },
        };

        private async Task<WorkflowHistory> FetchAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            CancellationToken cancellationToken) =>
            await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions()).WaitAsync(ExecutionDeadline, cancellationToken);

        private async Task<InvalidOperationException> DescribeTheUnfinishedRunAsync(
            WorkflowHandle<ReportJobWorkflow> handle,
            string reportJobId,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var failed = await FetchAsync(handle, cancellationToken);
            var described = await Client.WorkflowService.DescribeWorkflowExecutionAsync(
                Describe(handle.Id),
                new RpcOptions { CancellationToken = cancellationToken });
            var pending = string.Join(
                " | ",
                described.PendingActivities.Select(activity => $"{activity.ActivityType.Name} attempt {activity.Attempt}: {activity.LastFailure?.Message}"));
            return new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the recorded job {reportJobId} did not finish: {string.Join(", ", EventsOf(failed).Select(entry => entry.EventType.ToString()))}; pending: {pending}; {exception.Message}"),
                exception);
        }

        private static IEnumerable<Temporalio.Api.History.V1.HistoryEvent> EventsOf(WorkflowHistory history) => history.Events;
    }
}
