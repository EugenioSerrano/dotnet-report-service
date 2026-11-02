extern alias orchestration;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dapper;
using DotNet.Testcontainers.Containers;
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
using Temporalio.Client;
using Temporalio.Common;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The built API on real Kestrel, as its own process: a client sends the whole request
/// and closes the connection before reading any answer, and the job it created is
/// durable and starts. The request is sent over a raw socket, so the close is the
/// client's own and not a framework detail of the test's host — which is the point:
/// nothing in accepting a job observes the client's connection after the body is read.
/// </summary>
public sealed class JobAcceptanceOverRealServerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    private const string Consumer = "dev-consumer";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly int _hostPort = FreePort();

    private readonly int _apiPort = FreePort();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-server-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private IContainer? _engine;

    private Process? _api;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobAcceptanceOverRealServerTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
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

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-job-server")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);

        _api = StartApi();
        await Wait.UntilAsync(
            "the built API answering its health check",
            async token =>
            {
                using var probe = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_apiPort}") };
                using var response = await probe.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            try
            {
                _api.Kill(entireProcessTree: true);
                await _api.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // The process had already ended.
            }

            _api.Dispose();
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
    public async Task AClientThatClosesBeforeReadingItsAnswerStillLeavesAJobThatStarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenReadyUploadAsync(cancellationToken);
        await using var worker = Worker();
        using var workerClient = worker.CreateClient();
        await Wait.UntilAsync(
            "the orchestration worker answering its health check",
            async token =>
            {
                using var response = await workerClient.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        // The whole request goes out, and the socket is closed without a byte of the answer read.
        SendAndClose(uploadId);

        var reportJobId = await WaitForJobAsync(uploadId, cancellationToken);
        var workflowId = ReportJobWorkflow.WorkflowIdFor(reportJobId);
        await Wait.UntilAsync(
            $"the start request of {reportJobId} dispatched",
            async token =>
            {
                await using var connection = await _dataSource!.OpenConnectionAsync(token);
                return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                    "SELECT status FROM report_service.intents WHERE kind = 'start-job' AND subject_id = @ReportJobId",
                    new { ReportJobId = reportJobId },
                    cancellationToken: token)) == "DISPATCHED";
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        var client = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" });
        var history = await client.GetWorkflowHandle(workflowId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
        Assert.Contains(history.Events, entry => entry.EventType == Temporalio.Api.Enums.V1.EventType.WorkflowExecutionCompleted);
    }

    // The built API, as its own process, on the product's own settings.
    private Process StartApi()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ReportService.Api.dll"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{_apiPort}");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment[DatabaseOptions.ConnectionStringSetting.Replace(":", "__", StringComparison.Ordinal)] = _connectionString!;
        start.Environment[DatabaseOptions.ExpectedSchemaVersionSetting.Replace(":", "__", StringComparison.Ordinal)] = MigrationCatalog.ExpectedVersion;
        start.Environment[$"{ConsumerRegistryHosting.FileSetting.Replace(":", "__", StringComparison.Ordinal)}"] = registry;
        start.Environment["Storage__Provider"] = "filesystem";
        start.Environment["Storage__Filesystem__Root"] = Path.Combine(_directory, "storage");
        return Process.Start(start) ?? throw new InvalidOperationException("the built API could not be started");
    }

    // A raw request, written whole and followed by a close: no response is read at all.
    private void SendAndClose(string uploadId)
    {
        var body = $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""";
        var request = new StringBuilder()
            .Append("POST ").Append(CreateReportJobEndpoint.RoutePrefix).Append(" HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1:").Append(_apiPort.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append(ConsumerIdentity.HeaderName).Append(": ").Append(Consumer).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .Append(body)
            .ToString();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(IPAddress.Loopback, _apiPort);
        socket.Send(Encoding.ASCII.GetBytes(request));
        socket.Shutdown(SocketShutdown.Send);
        socket.Close();
    }

    private async Task<string> WaitForJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        string? reportJobId = null;
        await Wait.UntilAsync(
            "the job the disconnected client created",
            async token =>
            {
                await using var connection = await _dataSource!.OpenConnectionAsync(token);
                reportJobId = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                    "SELECT report_job_id FROM report_service.report_jobs WHERE upload_id = @UploadId",
                    new { UploadId = uploadId },
                    cancellationToken: token));
                return reportJobId is not null;
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        return reportJobId!;
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

    private async Task<string> GivenReadyUploadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
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
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(6), cancellationToken));
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
        return uploadId;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
