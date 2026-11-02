extern alias orchestration;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Testing;
using Temporalio.Worker;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Records the three job histories the hermetic suite replays, once, so that suite has
/// something to check. It is opt-in: it runs only when the environment variable names
/// the directory to write, so the integration command never runs it, and the ordinary
/// cases of this suite are untouched by it. It needs one engine and nothing else — the
/// jobs it runs live in the in-memory record stores — so the histories are the outcome
/// of real executions of the workflow and its two steps.
/// </summary>
public sealed class ReportJobHistoryCapture
{
    /// <summary>The variable that names the directory the captured histories are written to; without it the case is skipped.</summary>
    public const string OutputVariable = "REPORTSERVICE_CAPTURE_WORKFLOW_HISTORY";

    /// <summary>The names the hermetic suite reads the histories by.</summary>
    public static readonly string[] FileNames =
    [
        "report-job-ready.json",
        "report-job-rejected.json",
        "report-job-verifying.json",
    ];

    private const string Consumer = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan ExecutionDeadline = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task ThreeJobExecutionsAreRecordedAsTheHistoriesTheSuiteReplays()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(target))
        {
            Assert.Skip($"the capture runs once, when {OutputVariable} names the directory to write.");
        }

        Directory.CreateDirectory(target!);
        using var metrics = new OrchestrationMetrics();
        var uploads = new InMemoryUploadStore();
        var jobs = new InMemoryJobStore(uploads);
        var clock = new FixedTimeProvider(Now);
        var activities = new ReportJobActivities(jobs, clock, metrics, NullLogger<ReportJobActivities>.Instance);

        await using var environment = await WorkflowEnvironment.StartLocalAsync(new WorkflowEnvironmentStartLocalOptions());
        var client = environment.Client;
        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(ReportJobWorkflow.OrchestrationQueue)
                .AddWorkflow<ReportJobWorkflow>()
                .AddAllActivities(activities));

        using var stopWorker = new CancellationTokenSource();
        var running = worker.ExecuteAsync(stopWorker.Token);
        try
        {
            var ready = await GivenJobAsync(uploads, jobs, UploadState.Ready, "upload-00000000000000000000000000000001", cancellationToken);
            var rejected = await GivenJobAsync(uploads, jobs, UploadState.Rejected, "upload-00000000000000000000000000000002", cancellationToken);
            var verifying = await GivenJobAsync(uploads, jobs, UploadState.Verifying, "upload-00000000000000000000000000000003", cancellationToken);

            await RecordAsync(client, ready, Path.Combine(target!, FileNames[0]), cancellationToken);
            await RecordAsync(client, rejected, Path.Combine(target!, FileNames[1]), cancellationToken);
            await RecordAsync(client, verifying, Path.Combine(target!, FileNames[2]), cancellationToken);
        }
        finally
        {
            await stopWorker.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException)
            {
                // The worker was asked to stop, which is how a run of one ends.
            }
        }
    }

    /// <summary>The states the captured jobs' uploads are in when their executions run.</summary>
    private enum UploadState
    {
        Ready,
        Rejected,
        Verifying,
    }

    private static async Task RecordAsync(ITemporalClient client, string reportJobId, string path, CancellationToken cancellationToken)
    {
        var handle = await client.StartWorkflowAsync(
            (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
            new WorkflowOptions
            {
                Id = ReportJobWorkflow.WorkflowIdFor(reportJobId),
                TaskQueue = ReportJobWorkflow.OrchestrationQueue,
            });

        try
        {
            await handle.GetResultAsync().WaitAsync(ExecutionDeadline, cancellationToken);
        }
        catch (Exception exception)
        {
            var failed = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions()).WaitAsync(ExecutionDeadline, cancellationToken);
            var described = await client.WorkflowService.DescribeWorkflowExecutionAsync(
                new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
                {
                    Namespace = "default",
                    Execution = new Temporalio.Api.Common.V1.WorkflowExecution
                    {
                        WorkflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId),
                    },
                },
                new RpcOptions { CancellationToken = cancellationToken });
            var pending = string.Join(
                " | ",
                described.PendingActivities.Select(activity => $"{activity.ActivityType.Name} attempt {activity.Attempt}: {activity.LastFailure?.Message}"));
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the captured job {reportJobId} did not finish: {string.Join(", ", failed.Events.Select(entry => entry.EventType.ToString()))}; pending: {pending}; {exception.Message}"),
                exception);
        }

        var history = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(ExecutionDeadline, cancellationToken);
        await File.WriteAllTextAsync(path, history.ToJson(), cancellationToken);
    }

    // One job over one upload in the state the capture needs: the job is accepted while its
    // upload is still confirming and in time, and the upload then ends the way the case asks.
    private static async Task<string> GivenJobAsync(
        InMemoryUploadStore uploads,
        InMemoryJobStore jobs,
        UploadState state,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var stagingKey = ObjectKey.FromSegments("staging", uploadId, "content");
        var record = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            stagingKey,
            Now.AddHours(-1),
            Now.AddHours(1));
        await uploads.CreateAsync(record, cancellationToken);
        await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken);
        await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), Now.AddMinutes(-30), cancellationToken);
        await uploads.ConfirmAsync(
            uploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken);

        if (state is UploadState.Ready)
        {
            await uploads.CommitReadyAsync(
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
                    Now.AddMinutes(-20)),
                Now.AddMinutes(-20),
                cancellationToken);
        }

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0");
        var accepted = await jobs.AcceptAsync(acceptance, Now, cancellationToken);
        if (accepted is not JobAcceptanceResult.Accepted)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"the capture's job over {uploadId} had to be accepted"));
        }

        if (state is UploadState.Rejected)
        {
            await uploads.CommitRejectedAsync(
                uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, Now.AddMinutes(-10), cancellationToken);
        }

        return acceptance.ReportJobId;
    }
}
