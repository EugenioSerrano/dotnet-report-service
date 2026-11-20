extern alias orchestration;
extern alias pdf;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Testing.TemplatePackages;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource;
using ReportService.Uploads;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// A by-name job repeated after a newer version was taken, where the template step runs: the
/// product's API, its orchestration worker and its PDF worker hosted in this process over a
/// real PostgreSQL 18.6 database, a Temporal 1.9.1 engine, one storage root and the
/// deployment's template source seeded with the family the take is exercised on. The first
/// job is accepted by name with a key and pinned to the release it took; a newer release is
/// published and taken by a second job; the first request repeated with the same key is
/// answered its first job, still pinned to the version it took, with no new job, no new start
/// request and no new take, and the same request under a new key is a new job pinned to the
/// newer release.
/// </summary>
public sealed class JobIdempotencyAfterNewerVersionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string ConsumerA = "consumer-a";
    private const string Template = ByNameFixtures.Template;
    private const string First = ByNameFixtures.First;
    private const string Second = ByNameFixtures.Second;

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PinDeadline = TimeSpan.FromSeconds(120);

    private readonly PostgresFixture _fixture;
    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;
    private readonly LogCapture _logs = new();

    // The class's own root: kept short because the source class's keys are resolved under it by
    // the filesystem provider, which refuses a resolved path longer than its bound.
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "rsiv",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private EngineStart.Engine? _engine;
    private int _hostPort;
    private TemplateSourceClass? _source;
    private SourceClassStore? _sourceStore;
    private string? _database;
    private string? _connectionString;
    private NpgsqlDataSource? _dataSource;
    private WebApplicationFactory<Program>? _api;
    private WebApplicationFactory<OrchestrationWorker>? _orchestration;
    private WebApplicationFactory<PdfWorker>? _pdf;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public JobIdempotencyAfterNewerVersionTests(PostgresFixture fixture) => _fixture = fixture;

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

        _engine = await EngineStart.StartAsync(_engineImage, "temporal-job-idempotency", cancellationToken);
        _hostPort = _engine.HostPort;

        _source = await TemplateSourceClass.CreateAsync(SourceRoot());
        _sourceStore = new SourceClassStore(_source.Store);
        await ByNameFixtures.SeedAsync(_source, First);

        _api = Api();
        _orchestration = Orchestration();
        _pdf = Pdf();
        await WaitForHealthyAsync(_api.CreateClient(), ReadyDeadline, cancellationToken);
        await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
        await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _pdf?.Dispose();
        _orchestration?.Dispose();
        _api?.Dispose();
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        _logs.Dispose();
    }

    [Fact]
    public async Task AByNameRepeatKeepsTheVersionItsFirstJobTookAfterANewerOneIsTaken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The first job is accepted by name with a key and pinned to the release it took.
        var first = await AcceptedJobAsync(version: null, key: "K1", cancellationToken);
        Assert.Equal(First, await PinnedVersionAsync(first, cancellationToken));
        var positions = await SnapshotAsync(cancellationToken);

        // A newer release is published and taken, by a job that names it exactly.
        await ByNameFixtures.SeedAsync(_source!, Second);
        var second = await AcceptedJobAsync(version: Second, key: "K2", cancellationToken);
        Assert.Equal(Second, await PinnedVersionAsync(second, cancellationToken));
        Assert.NotEqual(first, second);

        var afterTake = await SnapshotAsync(cancellationToken);

        // The same request with the same key is answered its first job: still pinned to the
        // version it took, with no new job, no new start request and no new take.
        var repeat = await SubmitAsync(version: null, key: "K1", cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
        Assert.Equal(first, ReportJobIdOf(await repeat.Content.ReadAsStringAsync(cancellationToken)));
        Assert.Equal(First, await PinnedVersionAsync(first, cancellationToken));
        Assert.Equal(afterTake.Jobs, (await SnapshotAsync(cancellationToken)).Jobs);
        Assert.Equal(afterTake.References, (await SnapshotAsync(cancellationToken)).References);
        Assert.Equal(afterTake.Intents, (await SnapshotAsync(cancellationToken)).Intents);
        Assert.Equal(afterTake.TakenVersions, (await SnapshotAsync(cancellationToken)).TakenVersions);

        // The same request under a new key is a new job, pinned to the newer release.
        var newer = await AcceptedJobAsync(version: null, key: "K3", cancellationToken);
        Assert.NotEqual(first, newer);
        Assert.Equal(Second, await PinnedVersionAsync(newer, cancellationToken));
        Assert.Equal(positions.Jobs + 2, (await SnapshotAsync(cancellationToken)).Jobs);
    }

    private async Task<string> AcceptedJobAsync(string? version, string key, CancellationToken cancellationToken)
    {
        using var response = await SubmitAsync(version, key, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"a job by {(version is null ? "name" : version)} under {key} had to be accepted; the answer was {(int)response.StatusCode}: {body}");
        return ReportJobIdOf(body);
    }

    private async Task<HttpResponseMessage> SubmitAsync(string? version, string key, CancellationToken cancellationToken)
    {
        using var api = _api!.CreateClient();
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var template = version is null
            ? $$"""{"id":"{{Template}}"}"""
            : $$"""{"id":"{{Template}}","version":"{{version}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{{template}},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, ConsumerA);
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader.HeaderName, key);
        return await api.SendAsync(request, cancellationToken);
    }

    private static string ReportJobIdOf(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private async Task<string> PinnedVersionAsync(string reportJobId, CancellationToken cancellationToken)
    {
        string? version = null;
        try
        {
            await Wait.UntilAsync(
                $"the template pin of {reportJobId}",
                async token =>
                {
                    version = (await ReferenceAsync(reportJobId, token))?.ResolvedVersion;
                    return version is not null;
                },
                PinDeadline,
                TimeSpan.FromMilliseconds(250),
                cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"{reportJobId} was not pinned in {PinDeadline.TotalSeconds} s.{Environment.NewLine}"
                + string.Join(Environment.NewLine, _logs.Lines.TakeLast(60)));
        }

        return version!;
    }

    // The durable counts a repeat must leave exactly as they are: jobs, their references, the
    // start requests and the versions taken. A new workflow execution can only begin from a new
    // start request, so the intents' count covers it too.
    private async Task<(long Jobs, long References, long Intents, long TakenVersions)> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return (
            await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM report_service.report_jobs", cancellationToken: cancellationToken)),
            await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM report_service.template_references", cancellationToken: cancellationToken)),
            await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM report_service.intents", cancellationToken: cancellationToken)),
            await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM report_service.template_versions", cancellationToken: cancellationToken)));
    }

    private async Task<TemplateReferenceRow?> ReferenceAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TemplateReferenceRow>(new CommandDefinition(
            """
            SELECT report_job_id AS "ReportJobId", template_id AS "TemplateId",
                   reference_form AS "ReferenceForm", requested_version AS "RequestedVersion",
                   baseline_version AS "BaselineVersion", resolved_version AS "ResolvedVersion",
                   resolved_at AS "ResolvedAt"
              FROM report_service.template_references
             WHERE report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(
            registry,
            $$"""
            { "consumers": [
                { "consumerId": "{{ConsumerA}}", "active": true } ] }
            """);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });
    }

    private WebApplicationFactory<OrchestrationWorker> Orchestration() =>
        new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("Retention:Outputs", "01:00:00");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private WebApplicationFactory<PdfWorker> Pdf() =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", SourceRoot());
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAllKeyed<IObjectStore>(TemplateSourceStorage.StoreServiceKey);
                services.AddKeyedSingleton<IObjectStore>(TemplateSourceStorage.StoreServiceKey, _sourceStore!);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private string StorageRoot() => Path.Combine(_directory, "storage");

    private string SourceRoot() => Path.Combine(_directory, "template-source");

    private static async Task WaitForHealthyAsync(HttpClient client, TimeSpan deadline, CancellationToken cancellationToken) =>
        await Wait.UntilAsync(
            "a host answering /health",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private async Task<string> ReadyUploadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var uploads = new PostgresUploadStore(_dataSource!);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                ConsumerA,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);
        Assert.True(await uploads.TryStartTransferAsync(uploadId, ConsumerA, UploadContentEncoding.None, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId, UploadContentOutcome.Complete, 900, 900, new string('a', 64), now, cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, ConsumerA, null, now, now, now.AddHours(1), cancellationToken));
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    ConsumerA,
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

    // One reference row, as the table holds it.
    private sealed class TemplateReferenceRow
    {
        public string ReportJobId { get; set; } = string.Empty;

        public string TemplateId { get; set; } = string.Empty;

        public string ReferenceForm { get; set; } = string.Empty;

        public string? RequestedVersion { get; set; }

        public string? BaselineVersion { get; set; }

        public string? ResolvedVersion { get; set; }

        public DateTime? ResolvedAt { get; set; }

    }
}
