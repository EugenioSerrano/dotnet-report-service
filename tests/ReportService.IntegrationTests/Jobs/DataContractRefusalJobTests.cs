extern alias orchestration;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The refusal of a snapshot, end to end: for each document the data profile refuses, one
/// job accepted through the product's own API, run by the orchestration worker over a real
/// workflow engine, a real database and a temporary storage root, ends failed with the
/// code, the reason and the ordinal the reader reported, and leaves no partition, no stored
/// block, no index page and no artifact behind. A snapshot the profile admits is
/// partitioned and not refused. This class is kept out of a run that cannot print by its
/// trait, and it fails by name — rather than skipping — when the build it runs on has no
/// step that reads the snapshot, because a run that looks covering and is not is worse
/// than a run that says so.
/// </summary>
[Trait("Requires", "printing")]
public sealed class DataContractRefusalJobTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    private const string Consumer = "dev-consumer";

    /// <summary>The message a run whose build has no step that reads the snapshot fails with.</summary>
    public const string RefusalIsNotOnThisCommit =
        "the data step is not on this commit: the job's execution ended with the job still queued, so nothing read the snapshot and this run cannot be a covering run.";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan OutcomeDeadline = TimeSpan.FromMinutes(3);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly int _hostPort = FreePort();

    private readonly LogCapture _logs = new();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-data-contract-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private IContainer? _engine;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    /// <summary>Runs against the class's container.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public DataContractRefusalJobTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Every refused shape, each read plain and inside a complete gzip stream.</summary>
    public static TheoryData<string, string, int, bool> Refusals => new()
    {
        { "a header after the items", "header-not-first", 0, false },
        { "a header after the items", "header-not-first", 0, true },
        { "items before the header with the version last", "header-not-first", 0, false },
        { "items before the header with the version last", "header-not-first", 0, true },
        { "a header that is not an object", "type-mismatch", 0, false },
        { "a header that is not an object", "type-mismatch", 0, true },
        { "items that are not an array", "type-mismatch", 0, false },
        { "items that are not an array", "type-mismatch", 0, true },
        { "a member repeated in the header", "duplicate-property", 0, false },
        { "a member repeated in the header", "duplicate-property", 0, true },
        { "a member repeated in a middle record", "duplicate-property", 2, false },
        { "a member repeated in a middle record", "duplicate-property", 2, true },
        { "a member repeated in a nested object of a record", "duplicate-property", 2, false },
        { "a member repeated in a nested object of a record", "duplicate-property", 2, true },
        { "a member repeated in the last record", "duplicate-property", 2_000, false },
        { "a member repeated in the last record", "duplicate-property", 2_000, true },
    };

    /// <summary>Every admitted shape, read plain and inside a complete gzip stream.</summary>
    public static TheoryData<string, bool> Controls => new()
    {
        { "a valid snapshot", false },
        { "a valid snapshot", true },
        { "an empty items array", false },
        { "an empty items array", true },
    };

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

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-data-contract")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }

        _logs.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedSnapshotEndsTheJobFailedWithItsOwnReason(
        string shape,
        string reason,
        int ordinal,
        bool compressed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenReadyUploadAsync(Snapshot(shape), compressed, cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        await using var worker = Worker();
        await WaitForWorkerAsync(worker, cancellationToken);

        var state = await WaitForTerminalAsync(reportJobId, cancellationToken);

        Assert.Equal("FAILED", state.Status);
        Assert.Equal("DATA_INVALID", state.ErrorCode);
        Assert.NotNull(state.ErrorReason);
        Assert.Contains("reason=" + reason, state.ErrorReason, StringComparison.Ordinal);
        Assert.Contains(
            "ordinal=" + ordinal.ToString(CultureInfo.InvariantCulture),
            state.ErrorReason,
            StringComparison.Ordinal);

        // No value and no member name of the snapshot reaches the stored failure or a log line.
        Assert.DoesNotContain(Planted, state.ErrorReason, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(Planted, StringComparison.Ordinal));

        // Nothing of the snapshot was partitioned, stored or published.
        Assert.Equal(0, await PartitionsAsync(reportJobId, cancellationToken));
        Assert.Equal(0, await ArtifactsAsync(reportJobId, cancellationToken));
        Assert.DoesNotContain(StoredFiles(), path => path.Contains(reportJobId, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Controls))]
    public async Task AnAdmittedSnapshotIsPartitionedAndNotRefused(string shape, bool compressed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await GivenReadyUploadAsync(Snapshot(shape), compressed, cancellationToken);
        var reportJobId = await AcceptJobAsync(uploadId, cancellationToken);
        await using var worker = Worker();
        await WaitForWorkerAsync(worker, cancellationToken);

        var state = await WaitForTerminalAsync(reportJobId, cancellationToken);

        Assert.Equal("SUCCEEDED", state.Status);
        Assert.Null(state.ErrorCode);
        Assert.Equal(1, await PartitionsAsync(reportJobId, cancellationToken));
        Assert.Equal(0, await ArtifactsAsync(reportJobId, cancellationToken));
    }

    private const string Planted = "PLANTED-";

    private static string Snapshot(string shape) => shape switch
    {
        "a header after the items" => DocumentOf(
            Version,
            Root("items", "[" + Record(1) + "]"),
            Root("header", Header)),
        "items before the header with the version last" => DocumentOf(
            Root("items", "[" + Record(1) + "]"),
            Root("header", Header),
            Version),
        "a header that is not an object" => DocumentOf(
            Version,
            Root("header", "[" + Header + "]"),
            Root("items", "[]")),
        "items that are not an array" => DocumentOf(
            Version,
            Root("header", Header),
            Root("items", Record(1))),
        "a member repeated in the header" => DocumentOf(
            Version,
            Root("header", Duplicated(Header, "title", "\"" + Planted + "again\"")),
            Root("items", "[]")),
        "a member repeated in a middle record" => DocumentOf(
            Version,
            Root("header", Header),
            Root("items", "[" + string.Join(',', Record(1), Duplicated(Record(2, "\"middletag\":\"" + Planted + "middle\""), "middletag", "\"" + Planted + "again\""), Record(3)) + "]")),
        "a member repeated in a nested object of a record" => DocumentOf(
            Version,
            Root("header", Header),
            Root("items", "[" + string.Join(',', Record(1), Nested, Record(3)) + "]")),
        "a member repeated in the last record" => DocumentOf(
            Version,
            Root("header", Header),
            Root("items", "[" + string.Join(',', LastOfTwoThousand()) + "]")),
        "a valid snapshot" => DocumentOf(
            Version,
            Root("header", Header),
            Root("items", "[" + string.Join(',', Record(1), Record(2), Record(3), Record(4)) + "]")),
        "an empty items array" => DocumentOf(Version, Root("header", Header), Root("items", "[]")),
        _ => throw new InvalidOperationException($"the case '{shape}' names no snapshot"),
    };

    private const string Version = "\"contractVersion\":\"1.0\"";

    private static readonly string Header =
        "{\"title\":\"" + Planted + "title\",\"currency\":\"" + Planted + "currency\"}";

    private static readonly string Nested =
        "{\"sequence\":2,\"outer\":{\"inner\":{\"deeptag\":\"" + Planted + "deep\",\"deeptag\":\"" + Planted + "again\"}}}";

    private static string DocumentOf(params string[] rawProperties) =>
        "{" + string.Join(',', rawProperties) + "}";

    private static string Root(string name, string rawValue) => "\"" + name + "\":" + rawValue;

    private static string Record(int ordinal, string extra = "")
    {
        var body = "\"sequence\":" + ordinal.ToString(CultureInfo.InvariantCulture)
            + ",\"description\":\"" + Planted + "row " + ordinal.ToString(CultureInfo.InvariantCulture)
            + "\",\"amount\":" + ordinal.ToString(CultureInfo.InvariantCulture) + ".25";
        return "{" + body + (extra.Length == 0 ? string.Empty : "," + extra) + "}";
    }

    private static string Duplicated(string json, string member, string rawValue) =>
        json[..^1] + ",\"" + member + "\":" + rawValue + "}";

    private static IEnumerable<string> LastOfTwoThousand()
    {
        for (var ordinal = 1; ordinal < 2_000; ordinal++)
        {
            yield return Record(ordinal);
        }

        yield return Duplicated(Record(2_000, "\"finaltag\":\"" + Planted + "last\""), "finaltag", "\"" + Planted + "again\"");
    }

    private async Task WaitForWorkerAsync(WebApplicationFactory<OrchestrationWorker> worker, CancellationToken cancellationToken)
    {
        using var client = worker.CreateClient();
        await Wait.UntilAsync(
            "the orchestration worker answering its health check",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    private WebApplicationFactory<OrchestrationWorker> Worker() =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private async Task<JobRow> WaitForTerminalAsync(string reportJobId, CancellationToken cancellationToken)
    {
        JobRow? state = null;
        try
        {
            await Wait.UntilAsync(
                $"the job {reportJobId} to reach a terminal status",
                async token => (state = await JobAsync(reportJobId, token)) is { Status: "SUCCEEDED" or "FAILED" or "CANCELLED" },
                OutcomeDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            // The named failure below says what the job was doing instead.
        }

        if (state is null || state.Status is not ("SUCCEEDED" or "FAILED" or "CANCELLED"))
        {
            if (state?.Status == "QUEUED")
            {
                Assert.Fail(RefusalIsNotOnThisCommit);
            }

            Assert.Fail($"the job {reportJobId} had to reach a terminal status within {OutcomeDeadline}; it was {state?.Status}");
        }

        return state;
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

    private async Task<long> PartitionsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.partitions WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private async Task<long> ArtifactsAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.artifacts WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private IEnumerable<string> StoredFiles() =>
        Directory.EnumerateFiles(StorageRoot(), "*", SearchOption.AllDirectories);

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private async Task<string> AcceptJobAsync(string uploadId, CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
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

    private async Task<string> GivenReadyUploadAsync(string document, bool compressed, CancellationToken cancellationToken)
    {
        var uncompressed = Encoding.UTF8.GetBytes(document);
        var stored = compressed ? Gzip(uncompressed) : uncompressed;
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var snapshotKey = ObjectKey.FromSegments("snapshots", uploadId, "a");
        var written = await objects.WriteAsync(snapshotKey, new MemoryStream(stored, writable: false), cancellationToken);
        Assert.True(written.Succeeded, "the snapshot of the case had to be stored");

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
        Assert.True(await uploads.TryStartTransferAsync(
            uploadId,
            Consumer,
            compressed ? UploadContentEncoding.Gzip : UploadContentEncoding.None,
            now,
            cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, stored.Length, stored.Length, Sha256(stored), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(6), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    snapshotKey,
                    compressed ? UploadContentEncoding.Gzip : UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    Sha256(uncompressed),
                    uncompressed.Length,
                    written.Value!.Sha256,
                    written.Value.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    private static byte[] Gzip(byte[] document)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(document);
        }

        return compressed.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>One job as the database records it.</summary>
    private sealed class JobRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }

        public DateTime? FinishedAt { get; set; }
    }
}
