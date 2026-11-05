extern alias orchestration;
extern alias pdf;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Temporal;
using ReportService.Storage;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.TaskQueue.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using TakeTemplateVersionActivityOptions = pdf::ReportService.Workers.Pdf.Templates.TakeTemplateVersionActivityOptions;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Templates;

/// <summary>
/// The evidence of <c>report-pdf-rendering</c> against a real Temporal 1.9.1 engine: the queue
/// carries two registrations - the take's and the by-name resolution's - and one engine worker
/// serves both, from one identity; a worker whose run ends makes <c>/health</c> answer
/// <c>Unhealthy</c> and names the queue in its own data; and an engine that is lost and returns
/// on the same address is polled again without a restart of the process, and a job whose
/// template is taken after the return is pinned.
/// <para>
/// The PDF worker is hosted in this process on its real registrations, beside the API and the
/// orchestration worker the last case needs; the template source is the delivered loopback
/// double. (d) - a registration on an undeclared queue stops start-up naming the queue - is the
/// delivered <c>TemplateTakeRegistrationTests</c> case, re-run unchanged by the integration
/// cycle that covers this queue's work, and the take's own classes plus
/// <c>JointTemplateTakeTests</c> are re-run there as the regression.
/// </para>
/// </summary>
public sealed class PdfWorkerEngineWorkersTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Queue = TakeTemplateVersionActivityOptions.Queue;

    private const string Consumer = "consumer-a";

    private const string Template = "invoice-energy";

    private const string Version = "1.1.0";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan NotReadyDeadline = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan PinDeadline = TimeSpan.FromSeconds(90);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly LogCapture _logs = new();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-pdf-workers-it",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private EngineStart.Engine? _engine;

    private int _hostPort;

    private TemplateSourceHost? _source;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorker>? _orchestration;

    private WebApplicationFactory<PdfWorker>? _pdf;

    /// <summary>Runs against the class's server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public PdfWorkerEngineWorkersTests(PostgresFixture fixture) => _fixture = fixture;

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

        var database = await _fixture.CreateDatabaseAsync(cancellationToken);
        _connectionString = new NpgsqlConnectionStringBuilder(_fixture.Application(database))
        {
            Timeout = 5,
            CommandTimeout = PostgresJobStore.CommandTimeoutSeconds,
        }.ConnectionString;
        var migration = await MigrationRunner.RunAsync(_connectionString, MigrationCatalog.Scripts, cancellationToken);
        Assert.True(migration.Succeeded, string.Join("; ", migration.ErrorLines));
        _dataSource = NpgsqlDataSource.Create(_connectionString);

        await StartEngineAsync(cancellationToken);

        var stub = new GitHubTemplateSourceStub();
        stub.SeedPackage(Template, Version, TemplatePackageFixtures.Valid(Template, Version, owner: Consumer, shared: true));
        _source = await TemplateSourceHost.StartAsync(stub, cancellationToken);

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
        _logs.Dispose();
        foreach (var host in new IAsyncDisposable?[] { _pdf, _orchestration, _api })
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        }

        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_engine is not null)
        {
            await _engine.Container.DisposeAsync();
        }

        if (_source is not null)
        {
            await _source.DisposeAsync();
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
    public async Task OneWorkerIdentityPollsTheQueueAlthoughItCarriesTwoRegistrations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = _pdf!.CreateClient();

        // The two registrations of the same queue are served by one engine worker: the queue
        // reports one poller, and its identity is the process's own.
        var pollers = await PollersAsync(ReadyDeadline, cancellationToken);
        var poller = Assert.Single(pollers);

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var queues = health.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == TemporalConnectionHealthCheck.Name)
            .GetProperty("data").GetProperty("queues");
        Assert.Equal("worker running", queues.GetProperty(Queue).GetString());

        // Read once more: the single identity is stable, never a second worker appearing.
        Assert.Equal(poller.Identity, Assert.Single(await PollersAsync(ReadyDeadline, cancellationToken)).Identity);
    }

    [Fact]
    public async Task AWorkerWhoseRunEndsMakesReadinessUnhealthyNamingTheQueue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = _pdf!.Services.GetRequiredService<TemporalWorkerHost>();
        var states = _pdf.Services.GetRequiredService<EngineWorkerStates>();
        await PollersAsync(ReadyDeadline, cancellationToken);
        Assert.Equal(EngineWorkerStates.Running, states.Snapshot()[Queue]);

        // A run that ends writes the queue's stopped state: that is the host's own record.
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(EngineWorkerStates.Stopped, states.Snapshot()[Queue]);

        // Readiness names the queue over the endpoint, on a host whose queue is in that state -
        // the state a run's end writes, written here through the product's own seam so the
        // endpoint can be read after the run it reports has ended.
        await using var fresh = Pdf();
        using var client = fresh.CreateClient();
        await WaitForHealthyAsync(client, ReadyDeadline, cancellationToken);
        fresh.Services.GetRequiredService<EngineWorkerStates>().Set(Queue, EngineWorkerStates.Stopped);

        string body = string.Empty;
        await Wait.UntilAsync(
            $"/health answering {(int)HttpStatusCode.ServiceUnavailable}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                body = await response.Content.ReadAsStringAsync(token);
                return response.StatusCode == HttpStatusCode.ServiceUnavailable;
            },
            NotReadyDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

        Assert.Contains(Queue, body, StringComparison.Ordinal);
        Assert.Contains("worker stopped", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineThatReturnsIsPolledAgainWithoutARestartAndTheNextJobIsPinned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = _pdf!.CreateClient();
        var before = Assert.Single(await PollersAsync(ReadyDeadline, cancellationToken));
        var host = _pdf.Services.GetRequiredService<TemporalWorkerHost>();

        await _engine!.Container.StopAsync(cancellationToken);

        // The process notices the engine is gone and says so; readiness follows the connection.
        await WaitForLineAsync("engine connection lost", ReadyDeadline, cancellationToken);
        await WaitForStatusAsync(client, HttpStatusCode.ServiceUnavailable, NotReadyDeadline, cancellationToken);

        await StartEngineAsync(cancellationToken);

        await WaitForStatusAsync(client, HttpStatusCode.OK, ReadyDeadline, cancellationToken);
        var after = Assert.Single(await PollersAsync(ReadyDeadline, cancellationToken));
        await WaitForLineAsync("engine connection recovered", ReadyDeadline, cancellationToken);

        // No restart: the same host object, one start line, and the same worker identity.
        Assert.Same(host, _pdf.Services.GetRequiredService<TemporalWorkerHost>());
        Assert.Single(Lines("starting pdf-worker"));
        Assert.Single(Lines("engine connection lost"));
        Assert.Single(Lines("engine connection recovered"));
        Assert.Equal(before.Identity, after.Identity);

        // A job whose template is taken after the return is pinned: the queue serves the take
        // the job's step schedules, and the pin is read from the store.
        var reportJobId = await AcceptedJobAsync(cancellationToken);
        var pin = await PinnedAsync(reportJobId, cancellationToken);
        Assert.Equal(Version, pin.ResolvedVersion);
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            _engine = await EngineStart.StartAsync(_engineImage, "temporal-pdf-workers", cancellationToken);
            _hostPort = _engine.HostPort;
            return;
        }

        await EngineStart.RestartAsync(_engine, _engineImage, cancellationToken);
    }

    private async Task<IReadOnlyList<PollerInfo>> PollersAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        var engine = await TemporalClient.ConnectAsync(
            new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" });
        var pollers = new List<PollerInfo>();
        await Wait.UntilAsync(
            $"a poller on '{Queue}'",
            async token =>
            {
                var description = await engine.WorkflowService.DescribeTaskQueueAsync(
                    new DescribeTaskQueueRequest
                    {
                        Namespace = "default",
                        TaskQueue = new TaskQueue { Name = Queue },
                        TaskQueueType = TaskQueueType.Activity,
                    },
                    new RpcOptions { CancellationToken = token });
                pollers.Clear();
                pollers.AddRange(description.Pollers);
                return pollers.Count > 0;
            },
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return pollers;
    }

    private Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected, TimeSpan deadline, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"/health answering {(int)expected}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == expected;
            },
            deadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private Task WaitForLineAsync(string fragment, TimeSpan deadline, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"the log line '{fragment}'",
            _ => Task.FromResult(Lines(fragment).Count > 0),
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    private List<string> Lines(string fragment) =>
        [.. _logs.Lines.Where(line => line.Contains(fragment, StringComparison.Ordinal))];

    private async Task<string> AcceptedJobAsync(CancellationToken cancellationToken)
    {
        using var api = _api!.CreateClient();
        var uploadId = await ReadyUploadAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                $$"""{"template":{"id":"{{Template}}","version":"{{Version}}"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private async Task<string> ReadyUploadAsync(CancellationToken cancellationToken)
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
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(1), cancellationToken));
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

    private async Task<TemplatePin> PinnedAsync(string reportJobId, CancellationToken cancellationToken)
    {
        TemplatePin? pin = null;
        await Wait.UntilAsync(
            $"the template pin of {reportJobId}",
            async token =>
            {
                await using var connection = await _dataSource!.OpenConnectionAsync(token);
                var row = await connection.QuerySingleOrDefaultAsync<PinRow>(new CommandDefinition(
                    """
                    SELECT template_id AS "TemplateId", resolved_version AS "ResolvedVersion",
                           commit_sha AS "CommitSha", package_sha256 AS "PackageSha256",
                           engine AS "Engine", engine_version AS "EngineVersion",
                           schema_ref AS "SchemaRef", resolved_at AS "ResolvedAt"
                      FROM report_service.template_references
                     WHERE report_job_id = @ReportJobId AND resolved_at IS NOT NULL
                    """,
                    new { ReportJobId = reportJobId },
                    cancellationToken: token));
                pin = row?.Pin;
                return pin is not null;
            },
            PinDeadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return pin!;
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, $$"""{ "consumers": [ { "consumerId": "{{Consumer}}", "active": true } ] }""");
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
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
        });

    private WebApplicationFactory<PdfWorker> Pdf() =>
        new WebApplicationFactory<PdfWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{_hostPort}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", StorageRoot());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _source!.BaseUrl.AbsoluteUri);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:AllowInsecureLoopback", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private string StorageRoot() => Path.Combine(_directory, "storage");

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

    /// <summary>One pin, as the reference table holds it.</summary>
    private sealed class PinRow
    {
        public string TemplateId { get; set; } = string.Empty;

        public string ResolvedVersion { get; set; } = string.Empty;

        public string CommitSha { get; set; } = string.Empty;

        public string PackageSha256 { get; set; } = string.Empty;

        public string Engine { get; set; } = string.Empty;

        public string EngineVersion { get; set; } = string.Empty;

        public string SchemaRef { get; set; } = string.Empty;

        public DateTimeOffset ResolvedAt { get; set; }

        public TemplatePin? Pin => ResolvedVersion.Length == 0
            ? null
            : new TemplatePin(
                TemplateId,
                ResolvedVersion,
                CommitSha,
                PackageSha256,
                TemplateEngine.Scriban,
                EngineVersion,
                SchemaRef,
                ResolvedAt);
    }
}
