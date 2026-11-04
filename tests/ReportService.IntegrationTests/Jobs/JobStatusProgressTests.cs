extern alias orchestration;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;
using Temporalio.Client;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using PdfOutputActivityOptions = orchestration::ReportService.Workers.Orchestration.Jobs.PdfOutputActivityOptions;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// Following a report job while it runs: the owner's status read, sampled through the real
/// HTTP pipeline every hundred milliseconds against real PostgreSQL, a real workflow engine
/// and a temporary storage root, while the API host and the orchestration worker host are
/// each restarted. The data work is held out of the pollers until the case releases it, which
/// is how a job is observed at the step that reads its snapshot; once it runs, the partition
/// is committed and the totals the consumer sees are known. The class also reads back the
/// outcomes this increment can produce — the block maximum the partitioner refuses, an output
/// failed through the store's own writer, and a snapshot the reader refuses — on the job and
/// on its PDF output.
/// <para>
/// The templates this class's jobs are pinned to are installed before the first case, because
/// nothing on this commit takes a version: the pilot's own schema is packaged, written to the
/// object store and recorded in the version store, exactly as the other integration classes do.
/// The uploads are settled through the upload store's own record operation rather than by the
/// verification work, so the data work the case needs is the only work the data queue carries.
/// </para>
/// </summary>
public sealed class JobStatusProgressTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    /// <summary>The consumer the class's jobs belong to.</summary>
    private const string Consumer = "consumer-a";

    /// <summary>The template every job of this class is accepted for.</summary>
    private const string TemplateId = "pilot-simple";

    /// <summary>The version of that template whose taken copy this class holds.</summary>
    private const string Version = "1.0.0";

    /// <summary>The path of the schema inside the taken package.</summary>
    private const string SchemaPath = "schema.json";

    /// <summary>The pilot's own sample document of that template.</summary>
    private const string SamplePath = "sample.json";

    /// <summary>The interval every sample is taken at.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan HeldDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan ProgressDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-status-progress",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private int _hostPort;

    private IContainer? _engine;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    /// <summary>The pilot sample's header, as the document section carries it.</summary>
    private string _sampleHeader = "{}";

    /// <summary>The pilot sample's records, in the order the sample holds them.</summary>
    private IReadOnlyList<string> _sampleRecords = [];

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobStatusProgressTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        var sample = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", TemplateId, SamplePath),
            cancellationToken))!;
        _sampleHeader = sample["header"]!.ToJsonString();
        _sampleRecords = [.. sample["items"]!.AsArray().Select(record => record!.ToJsonString())];

        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-job-status-progress")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(GrpcPort, assignRandomHostPort: true)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);
        _hostPort = _engine.GetMappedPublicPort(GrpcPort);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_engineImage.Reference}) on port {_hostPort}",
            async token =>
            {
                var probe = await EngineClientAsync(token);
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);

        _api = Api();
        await SeedTakenVersionAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
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
    public async Task AJobHeldAtItsDataStepIsSampledAcrossTwoRestartsAndItsCountersOnlyRise()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadySnapshotAsync(Document(_sampleHeader, Records(1_000)), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        var samples = new List<Sample>();
        var gate = new DataQueueGate();

        // The data queue is withheld, so the run reaches the step that reads the snapshot and
        // the step never starts: the state a consumer polls while its data is being read.
        var worker = Worker(gate);
        try
        {
            using (var workerClient = worker.CreateClient())
            {
                await WaitForHealthyAsync(workerClient, cancellationToken);
            }

            using (var client = _api!.CreateClient())
            {
                await SampleUntilAsync(
                    client,
                    reportJobId,
                    samples,
                    "the job held at the step that reads its snapshot",
                    sample => sample.Status == "validating" && sample.Stage == "validating-data",
                    HeldDeadline,
                    cancellationToken);
            }

            // The state, the stage, the counters at zero and both totals unknown — present on
            // the wire and null, with no percentage: what a consumer sees while its data is read.
            var held = samples[^1];
            Assert.Equal("validating", held.Status);
            Assert.Equal("validating-data", held.Stage);
            Assert.Equal(0, held.RecordsRead);
            Assert.Null(held.RecordsTotal);
            Assert.Equal(0, held.BlocksCompleted);
            Assert.Null(held.BlocksTotal);
            Assert.Equal(0, held.PagesGenerated);
            Assert.Null(held.ErrorCode);
            var output = Assert.Single(held.Outputs);
            Assert.Equal("pdf", output.Format);
            Assert.Equal("pending", output.Status);
            Assert.Null(output.ArtifactId);
            Assert.Null(output.ErrorCode);

            // The API is restarted while the job is held: a new host of the same registrations
            // serves the read, and neither a counter nor the state moves back.
            await _api.DisposeAsync();
            _api = Api();
            using (var client = _api.CreateClient())
            {
                await WaitForHealthyAsync(client, cancellationToken);
                await SampleUntilAsync(
                    client,
                    reportJobId,
                    samples,
                    "the job read from the restarted API",
                    _ => true,
                    ReadyDeadline,
                    cancellationToken);
            }

            // The orchestration worker is restarted too: the run is the engine's, so the job
            // keeps the step it reached and the read answers the same.
            await worker.DisposeAsync();
            worker = Worker(gate);
            using (var workerClient = worker.CreateClient())
            {
                await WaitForHealthyAsync(workerClient, cancellationToken);
            }

            using (var client = _api.CreateClient())
            {
                await SampleUntilAsync(
                    client,
                    reportJobId,
                    samples,
                    "the job read after the worker restarted",
                    _ => true,
                    ReadyDeadline,
                    cancellationToken);
            }

            // The data queue starts polling: the snapshot is read and its partition committed,
            // and the samples show the counters and the totals the partition fixed.
            gate.Release();
            using (var client = _api.CreateClient())
            {
                await SampleUntilAsync(
                    client,
                    reportJobId,
                    samples,
                    "the committed partition of the job",
                    sample => sample.RecordsTotal == 1_000 && sample.RecordsRead == 1_000,
                    ProgressDeadline,
                    cancellationToken);
            }

            // The run reaches its last step: the output has not started, so nothing is written
            // and the job stays exactly as its steps left it — never as a terminal green.
            await WaitUntilRunCompletedAsync(reportJobId, cancellationToken);
            using (var client = _api.CreateClient())
            {
                await SampleUntilAsync(
                    client,
                    reportJobId,
                    samples,
                    "the job after its run completed",
                    _ => true,
                    ReadyDeadline,
                    cancellationToken);
            }
        }
        finally
        {
            await worker.DisposeAsync();
        }

        var settled = samples[^1];
        Assert.Equal("validating", settled.Status);
        Assert.Equal("validating-data", settled.Stage);
        Assert.Equal(1_000, settled.RecordsRead);
        Assert.Equal(1_000, settled.RecordsTotal);
        Assert.Equal(1, settled.BlocksTotal);
        Assert.Equal(0, settled.BlocksCompleted);
        Assert.Equal(0, settled.PagesGenerated);
        Assert.Null(settled.FinishedAt);
        var pdf = Assert.Single(settled.Outputs);
        Assert.Equal("pending", pdf.Status);
        Assert.Null(pdf.ArtifactId);

        AssertOnlyRisen(samples);

        // The instants the answer carries are the stored ones, in UTC with three digits.
        var instants = await InstantsAsync(reportJobId, cancellationToken);
        Assert.Equal(JobWireValues.TruncateToMillisecond(instants.CreatedAt), Parcel(settled.CreatedAt));
        Assert.NotNull(settled.StartedAt);
        Assert.Equal(JobWireValues.TruncateToMillisecond(instants.StartedAt!.Value), Parcel(settled.StartedAt));
        Assert.Null(instants.FinishedAt);
    }

    [Fact]
    public async Task AJobOverTheBlockMaximumEndsFailedWithItsCodeOnTheJobAndItsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadySnapshotAsync(Document(_sampleHeader, Records(2_001)), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(gate: null);
        using var workerClient = worker.CreateClient();
        await WaitForHealthyAsync(workerClient, cancellationToken);

        using var client = _api!.CreateClient();
        await SampleUntilAsync(
            client,
            reportJobId,
            [],
            "the job over the block maximum to end",
            sample => sample.Status is "failed" or "succeeded" or "cancelled",
            TerminalDeadline,
            cancellationToken);

        var answer = await ReadAsync(client, reportJobId, cancellationToken);
        Assert.Equal("failed", answer.Status);
        Assert.Equal("QUOTA_EXCEEDED", answer.ErrorCode);
        Assert.Null(answer.RecordsTotal);
        Assert.Equal(0, answer.BlocksCompleted);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("failed", output.Status);
        Assert.Equal("QUOTA_EXCEEDED", output.ErrorCode);
        Assert.Null(output.ArtifactId);
    }

    [Fact]
    public async Task AnOutputFailedThroughTheStoresOwnWriterIsReadBackOnTheJobAndItsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadySnapshotAsync(Document(_sampleHeader, Records(200)), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(gate: null);
        using var workerClient = worker.CreateClient();
        await WaitForHealthyAsync(workerClient, cancellationToken);

        using var client = _api!.CreateClient();
        await SampleUntilAsync(
            client,
            reportJobId,
            [],
            "the job's partition to commit",
            sample => sample.RecordsTotal == 200,
            ProgressDeadline,
            cancellationToken);

        // The output fails through the store's own writer, as the output's workflow fails a
        // verification, and the job is closed from that outcome by the step that closes it.
        var store = new PostgresJobStore(_dataSource!);
        var outputId = await OutputIdAsync(reportJobId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(OutputFailureOutcome.Applied, await store.FailOutputAsync(outputId, "PAGINATION_FAILED", now, cancellationToken));
        var closure = await store.FinalizeJobAsync(reportJobId, now.AddSeconds(1), cancellationToken);
        Assert.Equal(JobFinalizationOutcome.Applied, closure.Outcome);
        Assert.Equal(ReportJobStatus.Failed, closure.Status);

        var answer = await ReadAsync(client, reportJobId, cancellationToken);
        Assert.Equal("failed", answer.Status);
        Assert.Equal("PAGINATION_FAILED", answer.ErrorCode);
        Assert.NotNull(answer.FinishedAt);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("failed", output.Status);
        Assert.Equal("PAGINATION_FAILED", output.ErrorCode);
        Assert.Null(output.ArtifactId);

        var instants = await InstantsAsync(reportJobId, cancellationToken);
        Assert.Equal(JobWireValues.TruncateToMillisecond(instants.FinishedAt!.Value), Parcel(answer.FinishedAt));
    }

    [Fact]
    public async Task ASnapshotTheReaderRefusesEndsFailedWithItsCodeOnTheJobAndItsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await ReadySnapshotAsync(RefusedDocument(), cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);

        await using var worker = Worker(gate: null);
        using var workerClient = worker.CreateClient();
        await WaitForHealthyAsync(workerClient, cancellationToken);

        using var client = _api!.CreateClient();
        await SampleUntilAsync(
            client,
            reportJobId,
            [],
            "the refused snapshot to end the job",
            sample => sample.Status is "failed" or "succeeded" or "cancelled",
            TerminalDeadline,
            cancellationToken);

        var answer = await ReadAsync(client, reportJobId, cancellationToken);
        Assert.Equal("failed", answer.Status);
        Assert.Equal("DATA_INVALID", answer.ErrorCode);
        Assert.Null(answer.RecordsTotal);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("failed", output.Status);
        Assert.Equal("DATA_INVALID", output.ErrorCode);
        Assert.Null(output.ArtifactId);
    }

    // Consecutive samples of one job: a counter never falls, a total once known never changes,
    // the state never moves back and no stage other than the one the run entered is ever read.
    private static void AssertOnlyRisen(IReadOnlyList<Sample> samples)
    {
        Assert.NotEmpty(samples);
        string[] order = ["queued", "validating", "partitioning", "running", "finalizing", "succeeded", "failed"];

        for (var index = 1; index < samples.Count; index++)
        {
            var before = samples[index - 1];
            var after = samples[index];

            Assert.True(after.RecordsRead >= before.RecordsRead, $"recordsRead went from {before.RecordsRead} to {after.RecordsRead}");
            Assert.True(after.BlocksCompleted >= before.BlocksCompleted, $"blocksCompleted went from {before.BlocksCompleted} to {after.BlocksCompleted}");
            Assert.True(after.PagesGenerated >= before.PagesGenerated, $"pagesGenerated went from {before.PagesGenerated} to {after.PagesGenerated}");
            Assert.True(
                Array.IndexOf(order, after.Status) >= Array.IndexOf(order, before.Status),
                $"the status went from {before.Status} to {after.Status}");

            if (before.RecordsTotal is { } total)
            {
                Assert.Equal(total, after.RecordsTotal);
            }

            if (before.BlocksTotal is { } blocks)
            {
                Assert.Equal(blocks, after.BlocksTotal);
            }

            if (before.Stage is { } stage)
            {
                Assert.Equal(stage, after.Stage);
            }
        }
    }

    // The status read of one job, sampled on the interval until the case's own condition holds.
    private async Task SampleUntilAsync(
        HttpClient client,
        string reportJobId,
        List<Sample> samples,
        string what,
        Func<Sample, bool> stop,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        Sample? last = null;
        while (elapsed.Elapsed < deadline)
        {
            last = await SampleAsync(client, reportJobId, cancellationToken);
            samples.Add(last);
            if (stop(last))
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.Fail($"{what} was not observed within {deadline}: the last sample was {last}");
    }

    private async Task<Sample> SampleAsync(HttpClient client, string reportJobId, CancellationToken cancellationToken) =>
        await ReadAsync(client, reportJobId, cancellationToken);

    private static async Task<Sample> ReadAsync(HttpClient client, string reportJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "every status answer has to be uncacheable");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var progress = root.GetProperty("progress");
        return new Sample(
            root.GetProperty("status").GetString()!,
            root.GetProperty("stage") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("stage").GetString(),
            progress.GetProperty("recordsRead").GetInt64(),
            progress.GetProperty("recordsTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("recordsTotal").GetInt64(),
            progress.GetProperty("blocksCompleted").GetInt32(),
            progress.GetProperty("blocksTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("blocksTotal").GetInt32(),
            progress.GetProperty("pagesGenerated").GetInt64(),
            root.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorCode").GetString(),
            root.GetProperty("createdAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("createdAt").GetString(),
            root.GetProperty("startedAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("startedAt").GetString(),
            root.GetProperty("finishedAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("finishedAt").GetString(),
            [
                .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputSample(
                    element.GetProperty("format").GetString()!,
                    element.GetProperty("status").GetString()!,
                    element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString(),
                    element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString())),
            ]);
    }

    private static DateTimeOffset Parcel(string? value)
    {
        Assert.NotNull(value);
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private async Task<JobInstants> InstantsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<JobInstants>(new CommandDefinition(
            """
            SELECT created_at AS "CreatedAt", started_at AS "StartedAt", finished_at AS "FinishedAt"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<string> OutputIdAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT output_id FROM report_service.report_outputs WHERE report_job_id = @ReportJobId AND format = 'PDF'",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken)) ?? string.Empty;
    }

    private async Task WaitUntilRunCompletedAsync(string reportJobId, CancellationToken cancellationToken)
    {
        var client = await EngineClientAsync(cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);
        await Wait.UntilAsync(
            $"the run of {reportJobId} to complete",
            async token =>
            {
                var description = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(token);
                return description.Status == Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed;
            },
            TerminalDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
    }

    private Task<TemporalClient> EngineClientAsync(CancellationToken cancellationToken) =>
        TemporalClient.ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);

    // The upload is settled through the upload store's own record operation: nothing verifies it
    // on this class's worker, so the data work the job needs is the only work the data queue holds.
    private async Task<string> ReadySnapshotAsync(byte[] document, CancellationToken cancellationToken)
    {
        using var client = _api!.CreateClient();
        var uploadId = await CreateUploadAsync(client, cancellationToken);
        await SendContentAsync(client, uploadId, document, cancellationToken);
        using (var confirmed = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        await WithdrawVerificationRequestAsync(uploadId, cancellationToken);

        var objects = _api.Services.GetRequiredService<IObjectStore>();
        var key = ObjectKey.FromSegments("snapshots", uploadId, "content");
        var written = await objects.WriteAsync(key, new MemoryStream(document, writable: false), cancellationToken);
        Assert.True(written.Succeeded, "the snapshot of the case had to be written");

        var now = DateTimeOffset.UtcNow;
        var committed = await new PostgresUploadStore(_dataSource!).CommitReadyAsync(
            new SnapshotRecord(
                uploadId,
                Consumer,
                key,
                UploadContentEncoding.None,
                DataProfile.HeaderItemsV1,
                written.Value!.Sha256,
                written.Value.Size,
                written.Value.Sha256,
                written.Value.Size,
                now),
            now,
            cancellationToken);
        Assert.Equal(SnapshotCommitResult.Committed, committed);
        return uploadId;
    }

    // The confirmation left a verification request behind; withdrawing it keeps the upload
    // waiting for the record this class writes, as a deployment without the verification work would.
    private async Task WithdrawVerificationRequestAsync(string uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM report_service.intents WHERE kind = 'start-upload-verification' AND subject_id = @UploadId",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    private static async Task<string> CreateUploadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                """{"profile":"header-items-v1","mode":"API_STREAM"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task SendContentAsync(HttpClient client, string uploadId, byte[] document, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(document);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> AcceptJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        using var client = _api!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{"id":"{{TemplateId}}","version":"{{Version}}"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    // The records of a document of the case, built from the pilot's own records: the schema of
    // the pinned version admits every one of them, and the sequence is the record's position.
    private IReadOnlyList<string> Records(int count)
    {
        var records = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var item = JsonNode.Parse(_sampleRecords[index % _sampleRecords.Count])!.AsObject();
            item["sequence"] = index + 1;
            records.Add(item.ToJsonString());
        }

        return records;
    }

    private static byte[] Document(string header, IReadOnlyList<string> records)
    {
        var items = records.Count == 0 ? "[]" : "[" + string.Join(',', records) + "]";
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{{header}},"items":{{items}}}""");
    }

    // A document whose records come before its header: the profile refuses it at the items
    // member, before any record is captured, and the job ends with the profile's own code.
    private byte[] RefusedDocument() =>
        Encoding.UTF8.GetBytes(
            """{"contractVersion":"1.0","items":[{"sequence":1,"description":"","quantity":1,"unitPrice":1.00,"amount":1.00}],"header":"""
            + _sampleHeader
            + "}");

    private async Task WaitForHealthyAsync(HttpClient client, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            "/health answering 200",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(
            registry,
            """
            { "consumers": [
                { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
                { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true },
                { "consumerId": "consumer-c", "displayName": "Consumer C", "active": false } ] }
            """);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
    }

    // The worker's own registrations, with the data queue's poller held behind a gate when the
    // case asks for it: the orchestration queue runs, so the job reaches the step that reads its
    // snapshot, and the data queue does not, so that step never starts until the gate opens.
    private WebApplicationFactory<OrchestrationWorker> Worker(DataQueueGate? gate) =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory, "storage"));
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            if (gate is not null)
            {
                builder.ConfigureTestServices(services => services
                    .RemoveAll<EngineWorkerFactory>()
                    .AddSingleton<EngineWorkerFactory>((connection, plan) =>
                        plan.Queue == PdfOutputActivityOptions.DataQueue
                            ? new GatedWorker(new TemporalEngineWorker(connection, plan), gate)
                            : new TemporalEngineWorker(connection, plan)));
            }
        });

    // The taken version this class's jobs are pinned to, recorded in the class's own composition
    // before the first case: the pilot's schema is packaged, written under the content-addressed
    // key of its bytes, and the record carries the written package's own size and hash.
    private async Task SeedTakenVersionAsync(CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllBytesAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", TemplateId, SchemaPath),
            cancellationToken);
        var files = new List<PackageFileSource>
        {
            new(SchemaPath, _ => Task.FromResult<Stream>(new MemoryStream(schema, writable: false))),
        };

        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, cancellationToken);
        var bytes = sink.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(Consumer, TemplateId, Version, packageSha256);

        var objects = _api!.Services.GetRequiredService<IObjectStore>();
        using (var content = new MemoryStream(bytes, writable: false))
        {
            var written = await objects.WriteAsync(key, content, cancellationToken);
            Assert.True(written.Succeeded, "the taken package had to be written");
        }

        var versions = _api.Services.GetRequiredService<ITemplateVersionStore>();
        var inserted = await versions.TryInsertAsync(
            TemplateVersionRecords.Sample(
                TemplateId,
                Version,
                Consumer,
                shared: true,
                schemaRef: SchemaPath,
                packageSha256: packageSha256) with
            {
                PackageSize = bytes.Length,
                FileCount = files.Count,
                ObjectKey = key,
            },
            cancellationToken);
        Assert.True(inserted.WasInserted, "the taken version had to be recorded");
    }

    /// <summary>One answer of the status read, as the case sampled it.</summary>
    private sealed record Sample(
        string Status,
        string? Stage,
        long RecordsRead,
        long? RecordsTotal,
        int BlocksCompleted,
        int? BlocksTotal,
        long PagesGenerated,
        string? ErrorCode,
        string? CreatedAt,
        string? StartedAt,
        string? FinishedAt,
        IReadOnlyList<OutputSample> Outputs)
    {
        /// <inheritdoc />
        public override string ToString() =>
            $"{Status}/{Stage} read={RecordsRead} total={RecordsTotal} blocks={BlocksCompleted}/{BlocksTotal} pages={PagesGenerated}";
    }

    /// <summary>One requested output of an answer.</summary>
    private sealed record OutputSample(string Format, string Status, string? ArtifactId, string? ErrorCode);

    /// <summary>The instants a job's row holds.</summary>
    private sealed class JobInstants
    {
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset? StartedAt { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }
    }

    /// <summary>The gate the data queue's poller waits on until the case opens it.</summary>
    private sealed class DataQueueGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public Task Released() => _released.Task;
    }

    /// <summary>A worker that does not poll its queue until the gate opens.</summary>
    private sealed class GatedWorker(IRunningEngineWorker inner, DataQueueGate gate) : IRunningEngineWorker
    {
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await gate.Released().WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        public void UseConnection(IEngineConnection connection) => inner.UseConnection(connection);
    }
}
