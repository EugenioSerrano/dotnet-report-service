extern alias orchestration;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The refusal of a snapshot, all the way to the job a consumer asked for: a document is
/// stored through the upload path, a job is accepted for it and its execution is run by the
/// real orchestration worker against a real PostgreSQL, a real workflow engine and a
/// temporary storage root. Every refused case asserts two readings of one outcome — the row
/// the failure is stored in, and, once the job status read is served, the consumer's own
/// read of it — and the controls assert that a snapshot the profile admits is partitioned
/// and not refused.
/// <para>
/// This class is kept out of a run that cannot print by its trait. On a build whose job
/// execution does not yet read the snapshot, every case fails by name rather than skipping:
/// no stored failure can carry a reason there, so such a run cannot be a covering one. The
/// second reading of every case — <see cref="StatusReadNotServed"/> — is added before the
/// covering run is attempted.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class DataContractRefusalJobTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int GrpcPort = 7233;

    private const string Consumer = "dev-consumer";

    /// <summary>The template every job of this class is accepted for.</summary>
    private const string TemplateId = "pilot-grouped";

    /// <summary>The version of that template whose taken copy this class holds.</summary>
    private const string Version = "1.0.0";

    /// <summary>The path of the schema inside the taken package.</summary>
    private const string SchemaPath = "schema.json";

    /// <summary>The pilot's own sample document of that template.</summary>
    private const string SamplePath = "sample.json";

    /// <summary>The message a run on a build without the data-contract step fails with.</summary>
    public const string DataStepNotOnThisCommit =
        "the data-contract step is not on this commit: the job's execution ended without reading its snapshot, so no stored failure can name a refusal and this run cannot be a covering run.";

    /// <summary>
    /// The reading every case still owes: the job status read a consumer uses to learn its
    /// job failed. No job status read is served yet — only the template-version and the
    /// upload reads are mapped — so the second reading of this class is added, and this note
    /// removed, when that read is delivered, and before the covering run is attempted. A run
    /// of this class without it is not the run the acceptance criterion needs, and the
    /// message above says so.
    /// </summary>
    public const string StatusReadNotServed =
        "the consumer's job status read is not served on this commit: the class asserts the stored row only, so this run cannot be a covering run.";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan OutcomeDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan VerificationDeadline = TimeSpan.FromMinutes(3);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly LogCapture _logs = new();

    private readonly int _hostPort = FreePort();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-refusal-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private IContainer? _engine;

    private string? _database;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorker>? _worker;

    /// <summary>The pilot sample's header, as the profile document carries it.</summary>
    private string _sampleHeader = "{}";

    /// <summary>The pilot sample's records, in the order the snapshot carries them.</summary>
    private IReadOnlyList<string> _sampleRecords = [];

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public DataContractRefusalJobTests(PostgresFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);

        // The pilot's own sample carries the header and the records every rebuilt document
        // below is made of: the taken version's schema admits exactly its shape.
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", TemplateId, SamplePath),
            cancellationToken))!;
        _sampleHeader = sample["header"]!.ToJsonString();
        _sampleRecords = [.. sample["items"]!.AsArray().Select(record => record!.ToJsonString())];

        _database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(_database))
        {
            Timeout = 5,
        }.ConnectionString;

        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        _engine = ContainerSmoke.Builder(_engineImage, "temporal-refusal")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);

        _api = Api();
        _worker = Worker();
        await SeedTakenVersionAsync(cancellationToken);

        using var client = _worker.CreateClient();
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _worker?.Dispose();
        _api?.Dispose();
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
    public async Task AHeaderAfterTheItemsIsRefusedWithHeaderNotFirst() =>
        await AssertRefusedAsync(
            "header-not-first",
            0,
            DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"items\":[" + Record(1) + "]",
                "\"header\":" + Header("PLANTED-VALUE-r1", "PLANTED-NAME-r1")),
            "PLANTED-VALUE-r1",
            "PLANTED-NAME-r1");

    [Fact]
    public async Task ItemsBeforeTheHeaderWithTheVersionLastAreRefusedWithHeaderNotFirst() =>
        await AssertRefusedAsync(
            "header-not-first",
            0,
            DocumentOf(
                "\"items\":[" + Record(1) + "]",
                "\"header\":" + Header("PLANTED-VALUE-r2", "PLANTED-NAME-r2"),
                "\"contractVersion\":\"1.0\""),
            "PLANTED-VALUE-r2",
            "PLANTED-NAME-r2");

    [Fact]
    public async Task AHeaderThatIsNotAnObjectIsRefusedWithTypeMismatch() =>
        await AssertRefusedAsync(
            "type-mismatch",
            0,
            DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"header\":[{\"title\":\"PLANTED-VALUE-r3a\",\"nested\":{\"PLANTED-NAME-r3a\":1}}]",
                "\"items\":[]"),
            "PLANTED-VALUE-r3a",
            "PLANTED-NAME-r3a");

    [Fact]
    public async Task ItemsThatAreNotAnArrayAreRefusedWithTypeMismatch() =>
        await AssertRefusedAsync(
            "type-mismatch",
            0,
            DocumentOf(
                "\"contractVersion\":\"1.0\"",
                "\"header\":" + HeaderTitled("PLANTED-VALUE-r3b"),
                "\"items\":{\"sequence\":1,\"description\":\"PLANTED-VALUE-r3b-row\",\"PLANTED-NAME-r3b\":1,\"nested\":{\"PLANTED-NAME-r3b-row\":1}}"),
            "PLANTED-VALUE-r3b",
            "PLANTED-NAME-r3b",
            "PLANTED-VALUE-r3b-row",
            "PLANTED-NAME-r3b-row");

    [Fact]
    public async Task AMemberRepeatedInTheHeaderIsRefusedWithDuplicateProperty()
    {
        var header = Duplicated("{\"title\":\"PLANTED-VALUE-r4a\"}", "PLANTED-NAME-r4a");
        await AssertRefusedAsync("duplicate-property", 0, Document(header: header), "PLANTED-VALUE-r4a", "PLANTED-NAME-r4a");
    }

    [Fact]
    public async Task AMemberRepeatedInAMiddleRecordIsRefusedWithDuplicateProperty()
    {
        var second = Duplicated(_sampleRecords[1], "PLANTED-NAME-r4b");
        await AssertRefusedAsync(
            "duplicate-property",
            2,
            Document(HeaderTitled("PLANTED-VALUE-r4b"), _sampleRecords[0], second, _sampleRecords[2]),
            "PLANTED-VALUE-r4b",
            "PLANTED-NAME-r4b");
    }

    [Fact]
    public async Task AMemberRepeatedInANestedObjectOfARecordIsRefusedWithDuplicateProperty()
    {
        var innermost = Duplicated("{\"x\":1}", "PLANTED-NAME-r4c");
        var second = _sampleRecords[1][..^1] + ",\"outer\":{\"middle\":" + innermost + "}}";
        await AssertRefusedAsync(
            "duplicate-property",
            2,
            Document(HeaderTitled("PLANTED-VALUE-r4c"), _sampleRecords[0], second, _sampleRecords[2]),
            "PLANTED-VALUE-r4c",
            "PLANTED-NAME-r4c");
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfThreeRecordsIsRefusedWithDuplicateProperty()
    {
        var third = Duplicated(_sampleRecords[2], "PLANTED-NAME-r4d");
        await AssertRefusedAsync(
            "duplicate-property",
            3,
            Document(HeaderTitled("PLANTED-VALUE-r4d"), _sampleRecords[0], _sampleRecords[1], third),
            "PLANTED-VALUE-r4d",
            "PLANTED-NAME-r4d");
    }

    [Fact]
    public async Task AMemberRepeatedInTheLastOfTwoThousandRecordsIsRefusedWithDuplicateProperty()
    {
        var last = Duplicated(_sampleRecords[^1], "PLANTED-NAME-r4e");
        var records = Enumerable.Range(0, 1_999).Select(index => _sampleRecords[index % _sampleRecords.Count]).ToList();
        records.Add(last);
        await AssertRefusedAsync(
            "duplicate-property",
            2_000,
            Document(HeaderTitled("PLANTED-VALUE-r4e"), [.. records]),
            "PLANTED-VALUE-r4e",
            "PLANTED-NAME-r4e");
    }

    [Fact]
    public async Task AValidSnapshotIsPartitionedAndNotRefused() =>
        await AssertPartitionedAsync(Document(_sampleHeader, [.. _sampleRecords]));

    [Fact]
    public async Task AnEmptyItemsArrayIsPartitionedAndNotRefused() =>
        await AssertPartitionedAsync(Document(header: _sampleHeader));

    // One job per refused snapshot, plain and inside a complete gzip stream: the stored row
    // carries the reason and the ordinal of the case, no partition, block, index object or
    // artifact is written for it, and no marker of the case's own document reaches the stored
    // reason or a log line. For a duplicated member the marker is the repeated name itself.
    private async Task AssertRefusedAsync(string reason, int ordinal, byte[] document, params string[] markers)
    {
        for (var compressed = 0; compressed < 2; compressed++)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var uploadId = await StoreAsync(document, compressed == 1, cancellationToken);
            var reportJobId = await AcceptAsync(uploadId, cancellationToken);
            await WaitForOutcomeAsync(reportJobId, cancellationToken);

            var row = await RowAsync(reportJobId, cancellationToken);
            Assert.Equal("FAILED", row.Status);
            Assert.Equal("DATA_INVALID", row.ErrorCode);
            Assert.NotNull(row.ErrorReason);
            Assert.Contains("reason=" + reason, row.ErrorReason, StringComparison.Ordinal);
            Assert.Contains(
                "ordinal=" + ordinal.ToString(CultureInfo.InvariantCulture),
                row.ErrorReason,
                StringComparison.Ordinal);
            Assert.Equal(0L, await PartitionsAsync(reportJobId, cancellationToken));
            Assert.Equal(0L, await ArtifactsAsync(reportJobId, cancellationToken));
            Assert.Empty(StoredObjects(reportJobId, "block"));
            Assert.Empty(StoredObjects(reportJobId, "index"));

            var logs = string.Join('\n', _logs.Lines);
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, row.ErrorReason, StringComparison.Ordinal);
                Assert.DoesNotContain(marker, logs, StringComparison.Ordinal);
            }
        }
    }

    // A document the profile admits: the job commits its partition and is not refused.
    private async Task AssertPartitionedAsync(byte[] document)
    {
        for (var compressed = 0; compressed < 2; compressed++)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var uploadId = await StoreAsync(document, compressed == 1, cancellationToken);
            var reportJobId = await AcceptAsync(uploadId, cancellationToken);
            await WaitForOutcomeAsync(reportJobId, cancellationToken);

            var row = await RowAsync(reportJobId, cancellationToken);
            Assert.Equal(1L, await PartitionsAsync(reportJobId, cancellationToken));
            Assert.NotEqual("DATA_INVALID", row.ErrorCode);
        }
    }

    private async Task WaitForOutcomeAsync(string reportJobId, CancellationToken cancellationToken)
    {
        var terminal = false;
        try
        {
            await Wait.UntilAsync(
                $"the job {reportJobId} to reach a terminal status",
                async token => (await StatusAsync(reportJobId, token)) is "SUCCEEDED" or "FAILED" or "CANCELLED",
                OutcomeDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
            terminal = true;
        }
        catch (TimeoutException)
        {
            // Fall through with the named failure below: the status is read once more to say why.
        }

        if (!terminal && await StatusAsync(reportJobId, cancellationToken) == "QUEUED")
        {
            Assert.Fail(DataStepNotOnThisCommit);
        }

        Assert.True(terminal, $"the job {reportJobId} had to reach a terminal status within {OutcomeDeadline}");
    }

    private async Task<string> StoreAsync(byte[] document, bool compressed, CancellationToken cancellationToken)
    {
        using var client = _api!.CreateClient();
        var uploadId = await CreateUploadAsync(client, cancellationToken);
        await SendContentAsync(client, uploadId, compressed ? Gzip(document) : document, compressed, cancellationToken);

        using (var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative)))
        {
            request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
            using var confirmed = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        var status = await WaitForUploadAsync(client, uploadId, cancellationToken);
        Assert.Equal("ready", status.GetProperty("status").GetString());
        return uploadId;
    }

    private static async Task<string> CreateUploadAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                """{"mode":"API_STREAM","profile":"header-items-v1"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task SendContentAsync(
        HttpClient client,
        string uploadId,
        byte[] content,
        bool compressed,
        CancellationToken cancellationToken)
    {
        using var body = new ByteArrayContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (compressed)
        {
            // The service classifies a body's encoding from this header alone, and it only
            // decompresses a body declared as a gzip stream: this one is, so it says so. The
            // plain document declares nothing, which is how it is read today.
            body.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = body,
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> WaitForUploadAsync(
        HttpClient client,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var found = JsonDocument.Parse("{}").RootElement.Clone();
        await Wait.UntilAsync(
            "the stored snapshot of the upload",
            async token =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
                using var response = await client.SendAsync(request, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                found = document.RootElement.Clone();
                return found.GetProperty("status").GetString() is "ready" or "rejected";
            },
            VerificationDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        if (found.GetProperty("status").GetString() == "rejected")
        {
            Assert.Fail(
                "the snapshot was rejected at its upload, so it never reached a job: "
                + found.GetProperty("rejectionCode").GetString()
                + "/"
                + found.GetProperty("rejectionReason").GetString());
        }

        return found;
    }

    private async Task<string> AcceptAsync(string uploadId, CancellationToken cancellationToken)
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

    // The taken version this class's jobs are pinned to, recorded in the class's own
    // composition before the first test runs: the pilot's own schema is packaged through
    // the archive the take uses, written under the content-addressed key of its bytes, and
    // the record carries that written package's real size, SHA-256 and file count at the
    // path its schema is read from.
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

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
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
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private async Task<string> StatusAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken)) ?? string.Empty;
    }

    private async Task<JobRow> RowAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<JobRow>(new CommandDefinition(
            """
            SELECT status AS "Status", error_code AS "ErrorCode", error_reason AS "ErrorReason"
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

    // The objects this job wrote, if any: a block or an index page is stored under a key that
    // names the job, so a refused job leaves none of either.
    private IReadOnlyList<string> StoredObjects(string reportJobId, string kind)
    {
        var root = Path.Combine(_directory, "storage");
        if (!Directory.Exists(root))
        {
            return [];
        }

        var marker = Path.DirectorySeparatorChar + kind + Path.DirectorySeparatorChar + reportJobId + Path.DirectorySeparatorChar;
        return
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => path.Contains(marker, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static string Header(string valueMarker, string nameMarker) =>
        "{\"title\":\"" + valueMarker + "\",\"nested\":{\"" + nameMarker + "\":1}}";

    // The pilot's own header with its title carrying the case's value marker: the taken
    // schema requires a string there and admits the sample's own members, so the header a
    // rebuilt case carries is the sample's with that one value replaced.
    private string HeaderTitled(string title)
    {
        var header = JsonNode.Parse(_sampleHeader)!.AsObject();
        header["reportTitle"] = title;
        return header.ToJsonString();
    }

    private static byte[] DocumentOf(params string[] rawProperties) =>
        Encoding.UTF8.GetBytes("{" + string.Join(',', rawProperties) + "}");

    private static byte[] Document(string header = """{"title":"Monthly statement","currency":"EUR"}""", params string[] records)
    {
        var items = records.Length == 0 ? "[]" : "[" + string.Join(',', records) + "]";
        return Encoding.UTF8.GetBytes($$"""{"contractVersion":"1.0","header":{{header}},"items":{{items}}}""");
    }

    private static string Record(int ordinal) =>
        $$"""{"sequence":{{ordinal}},"description":"row {{ordinal}}","amount":{{ordinal}}.25}""";

    private static string Duplicated(string json, string member) =>
        json[..^1] + ",\"" + member + "\":1,\"" + member + "\":2}";

    private static byte[] Gzip(byte[] document)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(document, 0, document.Length);
        }

        return compressed.ToArray();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>One stored job, as the row a consumer's failure is read from.</summary>
    private sealed class JobRow
    {
        public string Status { get; set; } = string.Empty;

        public string? ErrorCode { get; set; }

        public string? ErrorReason { get; set; }
    }
}
