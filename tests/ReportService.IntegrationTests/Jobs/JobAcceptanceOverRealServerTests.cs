extern alias orchestration;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
using ReportService.Storage.Filesystem;
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
    private const string Consumer = "dev-consumer";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);

    /// <summary>How long the built API may take from its start to its health check.</summary>
    private static readonly TimeSpan ApiReadyDeadline = TimeSpan.FromSeconds(45);

    /// <summary>How long the disconnected client waits for the host's answer before dropping the socket (microseconds).</summary>
    private const int AnswerWait = 5_000_000;

    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    // Everything the child process writes, so a start that fails names its own reason
    // instead of leaving a health check to time out on a refused connection.
    private readonly StringBuilder _apiOutput = new();

    private Uri? _apiAddress;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-job-server-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;

    private int _hostPort;

    private Process? _api;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private RenderWorkerHost? _render;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobAcceptanceOverRealServerTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-job-server", cancellationToken);
        _hostPort = _engine.HostPort;

        _api = StartApi();
        await WaitForApiAsync(cancellationToken);

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
        // The render worker stops while the process that accepted the job is still there to be
        // killed in this class's own order.
        if (_render is not null)
        {
            await _render.DisposeAsync();
        }

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

        string reportJobId;
        try
        {
            reportJobId = await WaitForJobAsync(uploadId, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"{exception.Message}{Environment.NewLine}The built API said:{Environment.NewLine}{ApiOutput()}",
                exception);
        }
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
        await WaitUntilRunCompletedAsync(client, workflowId, cancellationToken);
        var history = await client.GetWorkflowHandle(workflowId)
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
        Assert.Contains(history.Events, entry => entry.EventType == Temporalio.Api.Enums.V1.EventType.WorkflowExecutionCompleted);

        // The job the disconnected client left behind started and ended on its own run's outcome.
        var job = (await JobAsync(reportJobId, cancellationToken))!;
        Assert.Contains(job.Status, new[] { "SUCCEEDED", "FAILED" });
        Assert.NotNull(job.StartedAt);
        Assert.NotNull(job.FinishedAt);
    }

    // The built API, as its own process, on the product's own settings. It is given port
    // zero, so the operating system assigns the port it binds: no other process can have
    // taken it between a probe and the start, and the address it reports is the one this
    // class dials. Its output is read as it arrives, so a start that ends early says why.
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
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(ApiContentRoot());
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment[DatabaseOptions.ConnectionStringSetting.Replace(":", "__", StringComparison.Ordinal)] = _connectionString!;
        start.Environment[DatabaseOptions.ExpectedSchemaVersionSetting.Replace(":", "__", StringComparison.Ordinal)] = MigrationCatalog.ExpectedVersion;
        start.Environment[$"{ConsumerRegistryHosting.FileSetting.Replace(":", "__", StringComparison.Ordinal)}"] = registry;
        start.Environment["Storage__Provider"] = "filesystem";
        start.Environment["Storage__Filesystem__Root"] = Path.Combine(_directory, "storage");
        var process = Process.Start(start) ?? throw new InvalidOperationException("the built API could not be started");
        process.OutputDataReceived += (_, received) => Record(received.Data);
        process.ErrorDataReceived += (_, received) => Record(received.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    // The directory the API reads its settings from. The test output holds one
    // appsettings.json per referenced project and only the last copy survives, so the child
    // is pointed at the API's own directory — the same one the in-process hosts use, as the
    // content-root manifest this test's output carries states it.
    private static string ApiContentRoot()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "MvcTestingAppManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Name.StartsWith("ReportService.Api,", StringComparison.Ordinal))
            {
                return entry.Value.GetString()
                    ?? throw new InvalidOperationException("the run's content-root manifest names no directory for the API.");
            }
        }

        throw new InvalidOperationException("the run's content-root manifest holds no entry for the API, so the built API cannot read its settings.");
    }

    // Waits for the address the process says it listens on, then for its health check.
    // Both waits are bounded, and a process that ended first is reported with what it
    // wrote, never as a refused connection that says nothing.
    private async Task WaitForApiAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (_api!.HasExited)
            {
                throw new InvalidOperationException(
                    $"the built API exited with code {_api.ExitCode.ToString(CultureInfo.InvariantCulture)} before answering its health check. Its output was:{Environment.NewLine}{ApiOutput()}");
            }

            var address = ListeningAddress();
            if (address is not null)
            {
                _apiAddress = address;
                try
                {
                    using var probe = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
                    using var response = await probe.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // The address is known and the host has not answered yet.
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The probe's own bound passed; the deadline below still governs the wait.
                }
            }

            if (elapsed.Elapsed >= ApiReadyDeadline)
            {
                throw new TimeoutException(
                    $"the built API answering its health check was not ready after {elapsed.Elapsed.TotalSeconds:F1} s (deadline {ApiReadyDeadline.TotalSeconds:F0} s). Its output was:{Environment.NewLine}{ApiOutput()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    // The address the host reports once it is bound. Nothing dials the process before the
    // line exists, so the wait never guesses a port.
    private Uri? ListeningAddress()
    {
        const string marker = "Now listening on: ";
        var output = ApiOutput();
        var at = output.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var start = at + marker.Length;
        var end = output.IndexOf('\n', start);
        var text = (end < 0 ? output[start..] : output[start..end]).Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var address) ? address : null;
    }

    private void Record(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_apiOutput)
        {
            _apiOutput.AppendLine(line);
        }
    }

    private string ApiOutput()
    {
        lock (_apiOutput)
        {
            return _apiOutput.ToString();
        }
    }

    // A raw request, written whole and followed by a close: no response is read at all.
    // The client half-closes, waits until the host has answered — the answer stays unread —
    // and then drops the connection. Closing the handle the instant the bytes are handed to
    // the stack can discard them before the host reads them, which would test the transport
    // rather than the service's promise.
    private void SendAndClose(string uploadId)
    {
        var body = $$"""{"template":{"id":"pilot-grouped"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""";
        var request = new StringBuilder()
            .Append("POST ").Append(CreateReportJobEndpoint.RoutePrefix).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(_apiAddress!.Authority).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append(ConsumerIdentity.HeaderName).Append(": ").Append(Consumer).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .Append(body)
            .ToString();

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(IPAddress.Loopback, _apiAddress.Port);
        socket.Send(Encoding.ASCII.GetBytes(request));
        Assert.True(
            socket.Poll(AnswerWait, SelectMode.SelectRead),
            "the built API answered nothing before the client dropped the connection");
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

    private static async Task WaitUntilRunCompletedAsync(
        TemporalClient client,
        string workflowId,
        CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            $"the run of {workflowId} to complete",
            async token =>
            {
                var description = await client.GetWorkflowHandle(workflowId).DescribeAsync().WaitAsync(token);
                return description.Status == Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed;
            },
            DispatchDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private async Task<JobRow?> JobAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", status AS "Status", started_at AS "StartedAt",
                   finished_at AS "FinishedAt", error_code AS "ErrorCode"
              FROM report_service.report_jobs
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
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

        // The document a real upload of this template's sample carries, read once and written
        // below into the root the built API is pointed at, so the process that reads the
        // snapshot finds the bytes its row names.
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
        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            document.LongLength,
            document.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(document)),
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(6), cancellationToken));

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

    /// <summary>One job row, as the table holds it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime? StartedAt { get; set; }

        public DateTime? FinishedAt { get; set; }

        public string? ErrorCode { get; set; }
    }
}
