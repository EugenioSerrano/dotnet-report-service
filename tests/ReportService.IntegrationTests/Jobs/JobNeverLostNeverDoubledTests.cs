extern alias orchestration;

using System.Globalization;
using System.Net;
using System.Text;
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
using ReportService.Storage.Filesystem;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using ReportJobActivities = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobActivities;
using ReportJobInput = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobInput;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The whole promise, on a build that can print: a job accepted while the orchestration
/// worker is stopped, the API stopped, the worker started — exactly one execution and the
/// job at a terminal status; and the same with the dispatcher stopped after the start and
/// before its own record, then restarted. This class is kept out of a run that cannot
/// print by its trait, and it fails by name — rather than skipping — when the steps that
/// take a ready input to its end are not part of the build it runs on, because a run that
/// looks covering and is not is worse than a run that says so.
/// </summary>
[Trait("Requires", "printing")]
public sealed class JobNeverLostNeverDoubledTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Consumer = "dev-consumer";

    /// <summary>The message a run on a build without the printing steps fails with.</summary>
    public const string PrintingNotOnThisCommit =
        "printing is not on this commit: the job's execution ended with the job still queued, so the steps that take a ready input to its end are not part of this build, and this run cannot be a covering run.";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan OutcomeDeadline = TimeSpan.FromMinutes(3);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-covering-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;

    private int _hostPort;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private RenderWorkerHost? _render;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobNeverLostNeverDoubledTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-job-covering", cancellationToken);
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
    public async Task AJobAcceptedWhileNothingServesItRunsOnceToATerminalStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenReadyUploadAsync(cancellationToken);

        // The API answers 202 and is gone before anything can serve the job.
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, cancellationToken);
        await WaitForDispatchAsync(reportJobId, cancellationToken);

        await AssertOneExecutionToTerminalStatusAsync(reportJobId, cancellationToken);
    }

    [Fact]
    public async Task AProcessThatDiedBetweenTheStartAndItsRecordStillLeavesOneTerminalJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenReadyUploadAsync(cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);

        // The execution exists and the request is still pending: a process that died between
        // its start and its own record looks exactly like this.
        var client = await EngineClientAsync(cancellationToken);
        var started = await client.StartWorkflowAsync(
            (ReportJobWorkflow workflow) => workflow.RunAsync(new ReportJobInput(reportJobId)),
            new WorkflowOptions { Id = workflowId, TaskQueue = ReportJobWorkflow.OrchestrationQueue });

        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await WaitForStatusAsync(workerClient, cancellationToken);
        await WaitForDispatchAsync(reportJobId, cancellationToken);

        var runId = await DispatchedRunAsync(reportJobId, cancellationToken);
        Assert.Equal(started.ResultRunId, runId);

        await AssertOneExecutionToTerminalStatusAsync(reportJobId, cancellationToken);
    }

    private async Task AssertOneExecutionToTerminalStatusAsync(string reportJobId, CancellationToken cancellationToken)
    {
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);
        var client = await EngineClientAsync(cancellationToken);
        var handle = client.GetWorkflowHandle(workflowId);
        await Wait.UntilAsync(
            $"the run of {workflowId}",
            async token => (await handle.DescribeAsync().WaitAsync(token)) is not null,
            ReadyDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        var history = await handle.FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(OutcomeDeadline, cancellationToken);
        Assert.Single(Activities(history, ReportJobActivities.ResolveInputName));

        var terminal = false;
        try
        {
            await Wait.UntilAsync(
                $"the job {reportJobId} to reach a terminal status",
                async token => (await JobStatusAsync(reportJobId, token)) is "SUCCEEDED" or "FAILED" or "CANCELLED",
                OutcomeDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
            terminal = true;
        }
        catch (TimeoutException)
        {
            // Fall through with the named failure below: the status is read once more to say why.
        }

        // A run whose execution ended with the job still queued is a build without printing:
        // it says so by name instead of reporting a covering run that is not one.
        if (!terminal && await JobStatusAsync(reportJobId, cancellationToken) == "QUEUED")
        {
            Assert.Fail(PrintingNotOnThisCommit);
        }

        Assert.True(terminal, $"the job {reportJobId} had to reach a terminal status within {OutcomeDeadline}");

        // The end this build gives the job: the input is taken, the data step commits its one block
        // of the sample's 40 records, and the render refuses the template because it declares page
        // totals - a capability this service does not have yet - so the job and its one output end
        // failed with that code and nothing is published. The success that would follow page
        // accounting is not asserted here: this build cannot reach it.
        var outcome = await TerminalOutcomeAsync(reportJobId, cancellationToken);
        Assert.Equal("FAILED", outcome.Status);
        Assert.Equal("UNSUPPORTED_CAPABILITY", outcome.ErrorCode);
        Assert.Equal(1, outcome.BlockCount);
        Assert.Equal(40L, outcome.RecordCount);

        var pdf = Assert.Single(await OutputsAsync(reportJobId, cancellationToken));
        Assert.Equal("PDF", pdf.Format);
        Assert.Equal("FAILED", pdf.Status);
        Assert.Equal("UNSUPPORTED_CAPABILITY", pdf.ErrorCode);
        Assert.False(pdf.HasArtifact, $"the refused output of {reportJobId} publishes nothing");

        Assert.Single(await ReferencesAsync(reportJobId, cancellationToken));
    }

    private static IReadOnlyList<Temporalio.Api.History.V1.HistoryEvent> Activities(WorkflowHistory history, string name) =>
        [.. history.Events
            .Where(entry => entry.EventType == Temporalio.Api.Enums.V1.EventType.ActivityTaskScheduled)
            .Where(entry => string.Equals(
                entry.ActivityTaskScheduledEventAttributes.ActivityType.Name,
                name,
                StringComparison.Ordinal))];

    private Task WaitForDispatchAsync(string reportJobId, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the start request of {reportJobId} dispatched",
            async token => await IntentStatusAsync(reportJobId, token) == "DISPATCHED",
            ReadyDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private Task WaitForStatusAsync(HttpClient client, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            "the orchestration worker answering its health check",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private async Task<string> JobStatusAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken)) ?? string.Empty;
    }

    private async Task<string?> IntentStatusAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.intents WHERE kind = 'start-job' AND subject_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<string?> DispatchedRunAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            """
            SELECT r.run_id
              FROM report_service.execution_references r
              JOIN report_service.intents i ON i.intent_id = r.intent_id
             WHERE i.kind = 'start-job' AND i.subject_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<string>> ReferencesAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return
        [
            .. await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT r.run_id
                  FROM report_service.execution_references r
                  JOIN report_service.intents i ON i.intent_id = r.intent_id
                 WHERE i.kind = 'start-job' AND i.subject_id = @ReportJobId
                """,
                new { ReportJobId = reportJobId },
                cancellationToken: cancellationToken)),
        ];
    }

    private async Task<TerminalOutcome> TerminalOutcomeAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<TerminalOutcome>(new CommandDefinition(
            """
            SELECT j.status AS "Status", j.error_code AS "ErrorCode",
                   p.block_count AS "BlockCount", p.record_count AS "RecordCount"
              FROM report_service.report_jobs j
              JOIN report_service.partitions p ON p.report_job_id = j.report_job_id
             WHERE j.report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<OutputOutcome>> OutputsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return
        [
            .. await connection.QueryAsync<OutputOutcome>(new CommandDefinition(
                """
                SELECT o.format AS "Format", o.status AS "Status", o.error_code AS "ErrorCode",
                       a.artifact_id IS NOT NULL AS "HasArtifact"
                  FROM report_service.report_outputs o
                  LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                 WHERE o.report_job_id = @ReportJobId
                """,
                new { ReportJobId = reportJobId },
                cancellationToken: cancellationToken)),
        ];
    }

    private sealed record TerminalOutcome(string Status, string ErrorCode, int BlockCount, long RecordCount);

    private sealed record OutputOutcome(string Format, string Status, string? ErrorCode, bool HasArtifact);

    private async Task<string> AcceptJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });

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
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

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

    private async Task<string> GivenReadyUploadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

        // The document a real upload of this template's sample carries, read once and
        // written below: the bytes and the row that names them come from the same array,
        // so the snapshot cannot describe content nobody stored.
        var document = File.ReadAllBytes(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", "pilot-grouped", "sample.json"));
        var uploads = new PostgresUploadStore(_dataSource!);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                Consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, now, cancellationToken));

        // The snapshot's object is written through the same store and root the worker reads
        // it with, and the row takes the size and SHA-256 the write answered. A row that
        // named bytes nobody wrote is a read that can only fail.
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>.Instance);
        var snapshotKey = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(document, writable: false);
        var written = await objects.WriteAsync(snapshotKey, content, cancellationToken);
        Assert.True(
            written.Succeeded,
            $"the snapshot had to be written: the store answered {written.Outcome} "
            + $"({written.Error?.Code ?? "no code"}: {written.Error?.Message ?? "no message"})");
        var snapshot = written.Value!;

        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            document.LongLength,
            snapshot.Size,
            snapshot.Sha256,
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(6), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    snapshotKey,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    snapshot.Sha256,
                    snapshot.Size,
                    snapshot.Sha256,
                    snapshot.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    private string StorageRoot() => Path.Combine(_directory, "storage");
}
