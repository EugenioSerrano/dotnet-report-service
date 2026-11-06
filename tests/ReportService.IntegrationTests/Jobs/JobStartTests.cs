extern alias orchestration;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The path the service promises, against real PostgreSQL, a real engine and a real
/// storage root: a job answered <c>202</c> while the orchestration worker is stopped is
/// started exactly once when the worker starts, also when a process died between the
/// start and its own record; a job whose upload is refused or ran out of time before
/// the worker starts ends with the upload's own outcome; and a job whose upload is
/// still confirming stays queued and is never failed.
/// <para>
/// The API and the worker are the product's own registrations, hosted in this process:
/// the API answers, the worker polls the engine and the database, and the engine is the
/// pinned development server in its own container.
/// </para>
/// </summary>
public sealed class JobStartTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;

    private int _hostPort;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private RenderWorkerHost? _render;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobStartTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        try
        {
            await OpenAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-job-start", cancellationToken);
        _hostPort = _engine.HostPort;

        _render = await RenderWorkerHost.StartAsync(
            _hostPort,
            _connectionString!,
            Path.Combine(_directory, "storage"),
            "pilot-grouped",
            ReadyDeadline,
            null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_render is not null)
        {
            await _render.DisposeAsync();
        }

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

    [Fact]
    public async Task AJobAcceptedWhileTheWorkerIsStoppedIsStartedExactlyOnceAndCheckedOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(UploadStatus.Ready, DateTimeOffset.UtcNow.AddHours(6), cancellationToken);

        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        // The API answered 202 and is gone; the worker never ran, so nothing is started yet.
        Assert.Equal("PENDING", (await IntentAsync(reportJobId, cancellationToken))!.Status);
        Assert.Null(await ExecutionAsync(workflowId, cancellationToken));

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForDispatchAsync(reportJobId, cancellationToken);

        var intent = await IntentAsync(reportJobId, cancellationToken);
        Assert.NotNull(intent);
        Assert.Equal("DISPATCHED", intent!.Status);
        var reference = Assert.Single(await ReferencesAsync(intent.IntentId, cancellationToken));
        var execution = await ExecutionAsync(workflowId, cancellationToken);
        Assert.NotNull(execution);
        Assert.Equal(reference.RunId, execution!.Value.RunId);

        // The check ran exactly once, and the ready input stops the run at its continuation
        // point: the job stays queued until the steps that print are delivered with it.
        var history = await HistoryAsync(workflowId, cancellationToken);
        Assert.Single(Activities(history, ReportJobActivities.ResolveInputName));
        Assert.Contains(history.Events, entry => entry.EventType == EventType.WorkflowExecutionCompleted);
        Assert.Equal("QUEUED", (await JobAsync(reportJobId, cancellationToken))!.Status);
    }

    [Fact]
    public async Task AProcessThatDiedBetweenTheStartAndItsRecordStillLeavesOneExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(UploadStatus.Ready, DateTimeOffset.UtcNow.AddHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        // A process that starts the execution and dies before it records the dispatch leaves
        // the request pending and the execution in place; the next process must find it, not
        // start a second one.
        var client = await EngineClientAsync(cancellationToken);
        var started = await client.StartWorkflowAsync(
            (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
            new WorkflowOptions { Id = workflowId, TaskQueue = ReportJobWorkflow.OrchestrationQueue });
        Assert.Equal("PENDING", (await IntentAsync(reportJobId, cancellationToken))!.Status);

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForDispatchAsync(reportJobId, cancellationToken);

        var reference = Assert.Single(await ReferencesAsync((await IntentAsync(reportJobId, cancellationToken))!.IntentId, cancellationToken));
        Assert.Equal(started.Id, reference.WorkflowId);
        Assert.Equal(started.ResultRunId, reference.RunId);
        Assert.Equal(reference.RunId, (await ExecutionAsync(workflowId, cancellationToken))!.Value.RunId);
        Assert.Single(Activities(await HistoryAsync(workflowId, cancellationToken), ReportJobActivities.ResolveInputName));
    }

    [Fact]
    public async Task AJobWhoseUploadIsRefusedBeforeTheWorkerStartsEndsWithTheUploadsCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(UploadStatus.Verifying, DateTimeOffset.UtcNow.AddHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        var uploads = new PostgresUploadStore(_dataSource!);
        Assert.True(await uploads.CommitRejectedAsync(
            uploadId, UploadRejectionCode.DataInvalid, UploadRejectionReason.Truncated, DateTimeOffset.UtcNow, cancellationToken));

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForOutcomeAsync(reportJobId, cancellationToken);

        var state = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", state!.Status);
        Assert.Equal("DATA_INVALID", state.ErrorCode);
        Assert.Equal("input-rejected", state.ErrorReason);
        Assert.NotNull(state.FinishedAt);

        var output = await OutputAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", output!.Status);
        Assert.Equal("DATA_INVALID", output.ErrorCode);
    }

    [Fact]
    public async Task AJobWhoseUploadRanOutOfTimeBeforeTheWorkerStartsEndsAsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(UploadStatus.Verifying, DateTimeOffset.UtcNow.AddMinutes(10), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        var uploads = new PostgresUploadStore(_dataSource!);
        var expired = await uploads.ExpireIfOverdueAsync(uploadId, DateTimeOffset.UtcNow.AddMinutes(11), cancellationToken);
        Assert.True(expired.Expired, "the upload had to be marked expired for the case to mean anything");

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);
        await WaitForOutcomeAsync(reportJobId, cancellationToken);

        var state = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", state!.Status);
        Assert.Equal("INPUT_NOT_FOUND", state.ErrorCode);
        Assert.Equal("input-expired", state.ErrorReason);
    }

    [Fact]
    public async Task AJobWhoseUploadIsStillConfirmingStopsQueuedAndIsNotFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenUploadAsync(UploadStatus.Verifying, DateTimeOffset.UtcNow.AddHours(6), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, HttpStatusCode.OK, cancellationToken);

        // The run has done everything it will do before its upload settles: a build with the
        // waiting stage starts its timer, and a build without it reaches the point after the
        // check and completes. Neither may end the job.
        await Wait.UntilAsync(
            $"the run of {workflowId} to reach the point where it waits or stops",
            async token =>
            {
                var history = await HistoryAsync(workflowId, token);
                return history.Events.Any(entry => entry.EventType is EventType.TimerStarted or EventType.WorkflowExecutionCompleted);
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        var history = await HistoryAsync(workflowId, cancellationToken);

        // A confirming upload never fails the job and never lets the run claim its input is
        // ready: the check runs, and how often it runs again while the upload confirms is
        // the stage that waits for it, not this case.
        Assert.NotEmpty(Activities(history, ReportJobActivities.ResolveInputName));
        Assert.Empty(Activities(history, ReportJobActivities.FailJobName));

        var state = await JobAsync(reportJobId, cancellationToken);
        Assert.Equal("QUEUED", state!.Status);
        Assert.Null(state.FinishedAt);
    }

    private static IReadOnlyList<Temporalio.Api.History.V1.HistoryEvent> Activities(
        Temporalio.Common.WorkflowHistory history,
        string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    // One upload in the state a job's data reference is judged in, driven through the record
    // store's own steps so each case starts from a state the service can reach.
    private async Task<string> GivenUploadAsync(UploadStatus status, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var uploads = new PostgresUploadStore(_dataSource!);
        var record = new UploadRecord(
            uploadId,
            Consumer,
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            UploadStatus.Created,
            ObjectKey.FromSegments("staging", uploadId, "content"),
            now,
            now.AddHours(1));
        await uploads.CreateAsync(record, cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, deadline, cancellationToken));

        if (status is UploadStatus.Ready)
        {
            Assert.Equal(
                SnapshotCommitResult.Committed,
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
                        now),
                    now,
                    cancellationToken));
        }

        if (status is UploadStatus.Verifying)
        {
            // The confirmation left a verification request behind, and this process's worker
            // would run it and settle the upload from a content the case never wrote. The
            // request is withdrawn so the upload stays confirming for as long as the case
            // reads its job, which is the state a job sees while its input is still in doubt.
            await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM report_service.intents WHERE kind = 'start-upload-verification' AND subject_id = @UploadId",
                new { UploadId = uploadId },
                cancellationToken: cancellationToken));
        }

        return uploadId;
    }

    // The request the product's own operation answers, sent to a host built from the product's
    // own registrations; the host is disposed before the worker starts, so the answer cannot be
    // what starts the job.
    private async Task<string> AcceptJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var api = Api();
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
        return api;
    }

    // The worker on its real registrations: its two queues, its dispatching, its verification
    // and the work of a job, against the same database, engine and storage root as the API.
    private WebApplicationFactory<OrchestrationWorker> Worker() =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });

    private async Task<TemporalClient> EngineClientAsync(CancellationToken cancellationToken) =>
        await TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    private async Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            $"/health answering {(int)expected}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == expected;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private Task WaitForDispatchAsync(string reportJobId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the start request of {reportJobId} dispatched",
            async token => (await IntentAsync(reportJobId, token))?.Status == "DISPATCHED",
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private Task WaitForOutcomeAsync(string reportJobId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token => (await JobAsync(reportJobId, token))?.Status is "FAILED" or "SUCCEEDED" or "CANCELLED",
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private async Task<(string WorkflowId, string RunId)?> ExecutionAsync(string workflowId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        try
        {
            var description = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(cancellationToken);
            return (workflowId, description.RunId);
        }
        catch (Temporalio.Exceptions.RpcException exception) when ((int)exception.Code == 5)
        {
            return null;
        }
    }

    private async Task<WorkflowHistory> HistoryAsync(string workflowId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        return await client.GetWorkflowHandle(workflowId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
    }

    private async Task<JobRow?> JobAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", status AS "Status", error_code AS "ErrorCode",
                   error_reason AS "ErrorReason", finished_at AS "FinishedAt"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<OutputRow?> OutputAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<OutputRow>(new CommandDefinition(
            """
            SELECT output_id AS "OutputId", status AS "Status", error_code AS "ErrorCode"
              FROM report_service.report_outputs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<IntentRow?> IntentAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<IntentRow>(new CommandDefinition(
            """
            SELECT intent_id AS "IntentId", status AS "Status", dispatched_at AS "DispatchedAt"
              FROM report_service.intents
             WHERE kind = 'start-job' AND subject_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<ReferenceRow>> ReferencesAsync(long intentId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return
        [
            .. await connection.QueryAsync<ReferenceRow>(new CommandDefinition(
                """
                SELECT workflow_id AS "WorkflowId", run_id AS "RunId"
                  FROM report_service.execution_references
                 WHERE intent_id = @IntentId
                """,
                new { IntentId = intentId },
                cancellationToken: cancellationToken)),
        ];
    }

    /// <summary>One job row, as the table holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }

        public DateTime? FinishedAt { get; set; }
    }

    /// <summary>One output row.</summary>
    private sealed class OutputRow
    {
        public string OutputId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }
    }

    /// <summary>One start request.</summary>
    private sealed class IntentRow
    {
        public long IntentId { get; set; }

        public string Status { get; set; } = string.Empty;

        public DateTime? DispatchedAt { get; set; }
    }

    /// <summary>One recorded execution.</summary>
    private sealed class ReferenceRow
    {
        public string WorkflowId { get; set; } = string.Empty;

        public string RunId { get; set; } = string.Empty;
    }
}
