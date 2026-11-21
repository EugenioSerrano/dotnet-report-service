extern alias orchestration;

using System.Globalization;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Common;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;
using Temporalio.Workflows;
using JobTransitionActivities = orchestration::ReportService.Workers.Orchestration.Jobs.JobTransitionActivities;
using PdfOutputActivities = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivities;
using PdfOutputStepContracts = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputStepContracts;
using PdfOutputStepOptions = orchestration::ReportService.Workers.Orchestration.Jobs.RenderPdfBlockStepOptions;
using ReportJobActivityOptions = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivityOptions;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;
using ReportPdfOutputInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportPdfOutputInput;
using ReportPdfOutputWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportPdfOutputWorkflow;
using RetentionOptions = orchestration::ReportService.Workers.Orchestration.Retention.RetentionOptions;
using IRenderPdfBlockStep = orchestration::ReportService.Workers.Orchestration.Jobs.IRenderPdfBlockStep;
using RenderPdfBlockStepInput = orchestration::ReportService.Workers.Orchestration.Jobs.RenderPdfBlockStepInput;
using RenderPdfBlockStepResult = orchestration::ReportService.Workers.Orchestration.Jobs.RenderPdfBlockStepResult;
using OrchestrationMetrics = orchestration::ReportService.Workers.Orchestration.Telemetry.OrchestrationMetrics;
using FinalizeJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.FinalizeJobInput;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The child execution's refusal, against a real Temporal 1.9.1 engine: the one PDF output of a
/// job is started, the block render is stubbed to raise the non-retryable
/// <c>PAGINATION_FAILED</c> the delivered step raises, and the child ends the output with that
/// code - it never publishes, and its own history schedules no publication. Once the child has
/// ended, the delivered finalisation step is run on the job through the same activity the parent
/// uses, and the job ends failed with the output's code.
/// <para>
/// A workflow executed against the dev server is an integration dependency, which is why this
/// class lives here and not in the hermetic project; the replay suite keeps the delivered
/// histories, and none of them is a refusal.
/// </para>
/// </summary>
public sealed class PdfOutputRefusalTests : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    private const string Consumer = "dev-consumer";

    private static readonly DateTimeOffset Now = new(2026, 11, 19, 3, 15, 0, TimeSpan.Zero);

    private static readonly string Hash = new('c', 64);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-pdf-output-refusal",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private EngineStart.Engine? _engine;

    private NpgsqlDataSource? _dataSource;

    private PostgresJobStore? _jobs;

    private int _sequence;

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PdfOutputRefusalTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The child ends the output with the code the failure carries, publishes nothing, and the
    /// delivered finalisation closes the job failed with that same code.
    /// </summary>
    [Fact]
    public async Task TheChildEndsTheOutputWithTheCodeAndNeverPublishes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await OpenAsync(cancellationToken);
        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_engine!.HostPort}") { Namespace = "default" });
        var metrics = new OrchestrationMetrics(MeterIdentity.ForHost("integration-tests"));
        var transitions = new JobTransitionActivities(_jobs!, TimeProvider.System, metrics, NullLoggerFactory.Instance, NullLogger<JobTransitionActivities>.Instance);
        var outputs = new PdfOutputActivities(
            _jobs!,
            TimeProvider.System,
            Options.Create(new RetentionOptions { Outputs = TimeSpan.FromHours(1) }),
            NullLoggerFactory.Instance,
            NullLogger<PdfOutputActivities>.Instance);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var orchestration = new TemporalWorker(
            client,
            new TemporalWorkerOptions(ReportJobWorkflow.OrchestrationQueue)
                .AddWorkflow<ReportPdfOutputWorkflow>()
                .AddWorkflow<FinalizeOnceForRefusal>()
                .AddAllActivities(transitions)
                .AddAllActivities(outputs));
        using var rendering = new TemporalWorker(
            client,
            new TemporalWorkerOptions(PdfOutputStepOptions.Queue)
                .AddAllActivities(new RefusingRenderStep()));
        var runs = new[] { orchestration.ExecuteAsync(stop.Token), rendering.ExecuteAsync(stop.Token) };

        try
        {
            var acceptance = await AcceptedAsync(cancellationToken);
            var handle = await client.StartWorkflowAsync(
                (ReportPdfOutputWorkflow workflow) => workflow.RunAsync(
                    new ReportPdfOutputInput(acceptance.ReportJobId, acceptance.OutputId)),
                new WorkflowOptions
                {
                    Id = ReportPdfOutputWorkflow.WorkflowIdFor(acceptance.ReportJobId),
                    TaskQueue = ReportJobWorkflow.OrchestrationQueue,
                });
            await handle.GetResultAsync().WaitAsync(Deadline, cancellationToken);

            // The output ended with the failure's code, and its own history holds one failure
            // step and no publication at all.
            await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
            var row = await connection.QuerySingleAsync<OutputRow>(new CommandDefinition(
                "SELECT status AS \"Status\", error_code AS \"ErrorCode\" FROM report_service.report_outputs WHERE output_id = @OutputId",
                new { acceptance.OutputId },
                cancellationToken: cancellationToken));
            Assert.Equal("FAILED", row.Status);
            Assert.Equal("PAGINATION_FAILED", row.ErrorCode);

            var history = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions()).WaitAsync(cancellationToken);
            var failure = Assert.Single(Activities(history, JobTransitionActivities.FailOutputName));
            var input = failure.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
            Assert.Contains(acceptance.OutputId, input, StringComparison.Ordinal);
            Assert.Contains("PAGINATION_FAILED", input, StringComparison.Ordinal);
            Assert.Empty(Activities(history, PdfOutputActivities.PublishOutputName));

            // The parent's delivered finalisation, on the job the child left behind: the same
            // activity the parent's last step runs, started on the same queue.
            var finalize = await client.StartWorkflowAsync(
                (FinalizeOnceForRefusal workflow) => workflow.RunAsync(acceptance.ReportJobId),
                new WorkflowOptions
                {
                    Id = FinalizeOnceForRefusal.WorkflowType + "/" + acceptance.ReportJobId,
                    TaskQueue = ReportJobWorkflow.OrchestrationQueue,
                });
            var closed = await finalize.GetResultAsync().WaitAsync(Deadline, cancellationToken);
            Assert.Equal(JobFinalizationOutcome.Applied, closed.Outcome);
            Assert.Equal(ReportJobStatus.Failed, closed.Status);
            Assert.Equal("PAGINATION_FAILED", closed.ErrorCode);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(runs);
        }
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _jobs = new PostgresJobStore(_dataSource);
        _engine = await EngineStart.StartAsync(_engineImage, "temporal-pdf-output-refusal", cancellationToken);
    }

    // A job accepted over an upload driven to ready through the upload store's own steps.
    private async Task<JobAcceptance> AcceptedAsync(CancellationToken cancellationToken)
    {
        var uploads = new PostgresUploadStore(_dataSource!);
        var uploadId = UploadIdentifier.Prefix + (++_sequence).ToString("x32", CultureInfo.InvariantCulture);
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
        Assert.True(await uploads.TryStartTransferAsync(
            uploadId, Consumer, UploadContentEncoding.None, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 2_048, 2_048, Hash, Now.AddMinutes(-30), cancellationToken));
        Assert.True(await uploads.ConfirmAsync(
            uploadId, Consumer, null, Now.AddMinutes(-30), Now.AddMinutes(-30), Now.AddHours(5), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    ObjectKey.FromSegments("snapshots", uploadId, "a"),
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    Hash,
                    2_048,
                    Hash,
                    2_048,
                    Now.AddMinutes(-20)),
                Now.AddMinutes(-20),
                cancellationToken));

        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            Consumer,
            uploadId,
            "pilot-simple",
            TemplateReferenceForm.Exact,
            "1.0.0",
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await _jobs!.AcceptAsync(acceptance, Now, cancellationToken));
        return acceptance;
    }

    private static IReadOnlyList<HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    private sealed class OutputRow
    {
        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// The block render, as the refusal the delivered step raises: a document that failed its
    /// check ends the step non-retryably with <c>PAGINATION_FAILED</c> and the reason beside it.
    /// The method carries the name the PDF worker registers the render under, so the child's
    /// schedule is served here instead of on the worker's own queue.
    /// </summary>
    private sealed class RefusingRenderStep
    {
        [Activity(PdfOutputStepContracts.RenderPdfBlock)]
        public Task<RenderPdfBlockStepResult> RenderPdfBlock(RenderPdfBlockStepInput input) =>
            throw new ApplicationFailureException(
                "PAGINATION_FAILED reason=row-without-text record=2",
                errorType: "PAGINATION_FAILED",
                nonRetryable: true);
    }
}

/// <summary>
/// The parent's last step, run where the parent runs it: one delivered finalisation activity on a
/// job the child has already ended, so the job's own closure is the delivered rule's.
/// </summary>
[Workflow(WorkflowType)]
internal sealed class FinalizeOnceForRefusal
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "FinalizeOnceForRefusal";

    /// <summary>Closes one job from the outcome of its outputs.</summary>
    /// <param name="reportJobId">The job to close.</param>
    [WorkflowRun]
    public Task<JobFinalizationResult> RunAsync(string reportJobId) =>
        Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.FinalizeJobAsync(new FinalizeJobInput(reportJobId)),
            ReportJobActivityOptions.Create());
}
