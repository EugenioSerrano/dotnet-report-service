extern alias orchestration;
extern alias pdf;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Migrations;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using Temporalio.Client;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// One running composition of the three processes a report job needs, against real
/// dependencies: a PostgreSQL 18.6 database brought to the expected version by the product's
/// migrations, a Temporal 1.9.1 engine, one storage root, and the product's own API,
/// orchestration worker and render worker hosted on their real registrations. The template
/// source is the delivered loopback double, seeded with the sample template at two versions
/// and with the two versions a job must refuse.
/// <para>
/// The rig is the shared grounding of the worker-level cases: each case drives jobs through
/// the product's own API, waits for the terminal status from the composition's database - no
/// status read exists for this path - and reads the artifact the run published through the
/// verifying storage reader.
/// </para>
/// </summary>
internal sealed class PrintingRig : IAsyncDisposable
{
    /// <summary>The consumer every case submits for.</summary>
    public const string Consumer = "consumer-a";

    /// <summary>The sample template every case prints.</summary>
    public const string Template = "pilot-simple";

    /// <summary>The version the sample template ships as the first release.</summary>
    public const string First = "1.0.0";

    /// <summary>A later version of the same template, taken while a job is pinned to the first.</summary>
    public const string Second = "1.1.0";

    /// <summary>The version whose manifest declares no field it prints as it is.</summary>
    public const string NoAnchors = "1.2.0";

    /// <summary>The version whose manifest declares page subtotals.</summary>
    public const string PageTotals = "1.3.0";

    private const int GrpcPort = 7233;

    private static readonly TimeSpan EngineStartDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly int _hostPort = FreePort();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "rs12",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private readonly LogCapture _logs = new();

    private IContainer? _engine;

    private TemplateSourceHost? _source;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorker>? _orchestration;

    private WebApplicationFactory<PdfWorker>? _pdf;

    private PrintingRig(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The double every take and resolution of these cases reads.</summary>
    public GitHubTemplateSourceStub Source { get; private set; } = null!;

    /// <summary>The storage the composition writes to.</summary>
    public IObjectStore Objects { get; private set; } = null!;

    /// <summary>The job records the composition writes.</summary>
    public IJobStore Jobs { get; private set; } = null!;

    /// <summary>The database the composition uses.</summary>
    public NpgsqlDataSource DataSource => _dataSource!;

    /// <summary>The root the composition's storage keeps its objects under.</summary>
    public string StorageRoot => Path.Combine(_directory, "storage");

    /// <summary>Starts the composition on the case's own database and engine.</summary>
    /// <param name="fixture">The PostgreSQL container the class owns.</param>
    /// <param name="startOrchestration">Whether the job's own worker starts now; a case that pins two jobs starts it later.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task<PrintingRig> StartAsync(
        PostgresFixture fixture,
        bool startOrchestration = true,
        CancellationToken cancellationToken = default)
    {
        var rig = new PrintingRig(fixture);
        await rig.OpenAsync(startOrchestration, cancellationToken);
        return rig;
    }

    /// <summary>Starts the orchestration worker, which serves the job's execution and its data step.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StartOrchestrationAsync(CancellationToken cancellationToken)
    {
        _orchestration ??= Orchestration();
        await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
    }

    /// <summary>Creates a ready upload of <paramref name="document"/> and answers its identifier.</summary>
    /// <param name="document">The snapshot's bytes.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<string> ReadyUploadAsync(byte[] document, CancellationToken cancellationToken)
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
            uploadId,
            UploadContentOutcome.Complete,
            document.LongLength,
            document.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(document)),
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(1), cancellationToken));

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(document, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    Consumer,
                    key,
                    UploadContentEncoding.None,
                    DataProfile.HeaderItemsV1,
                    written.Value!.Sha256,
                    document.LongLength,
                    written.Value.Sha256,
                    written.Value.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    /// <summary>Submits one job through the product's own API and answers its identifier.</summary>
    /// <param name="uploadId">The ready upload it reports on.</param>
    /// <param name="version">The exact version it names.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<string> SubmitAsync(string uploadId, string? version, CancellationToken cancellationToken)
    {
        using var api = _api!.CreateClient();
        var template = version is null
            ? "{\"id\":\"" + Template + "\"}"
            : "{\"id\":\"" + Template + "\",\"version\":\"" + version + "\"}";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":" + template + ",\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[\"pdf\"]}",
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

    /// <summary>Waits for the job to reach a terminal status and answers what the record says.</summary>
    /// <param name="reportJobId">The job to wait for.</param>
    /// <param name="deadline">How long the job may take.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<JobStateRow> WaitForTerminalAsync(string reportJobId, TimeSpan deadline, CancellationToken cancellationToken)
    {
        JobStateRow? row = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to end",
            async token =>
            {
                row = await ReadAsync(reportJobId, token);
                return row?.Status is "SUCCEEDED" or "FAILED" or "CANCELLED";
            },
            deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return row!;
    }

    /// <summary>Reads one job's state once, as the composition recorded it.</summary>
    /// <param name="reportJobId">The job to read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<JobStateRow?> ReadAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JobStateRow>(new CommandDefinition(
            """
            SELECT j.status AS "Status", j.error_code AS "ErrorCode", j.error_reason AS "ErrorReason",
                   o.status AS "OutputStatus", o.error_code AS "OutputErrorCode",
                   a.artifact_id AS "ArtifactId", a.object_key AS "ObjectKey",
                   a.sha256 AS "Sha256", a.size_bytes AS "SizeBytes", a.page_count AS "PageCount",
                   p.block_count AS "BlockCount", p.record_count AS "RecordCount",
                   c.template_package_sha256 AS "TemplatePackageSha256", c.page_count AS "PartPages"
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
              LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
              LEFT JOIN report_service.partitions p ON p.report_job_id = j.report_job_id
              LEFT JOIN report_service.pdf_part_commits c ON c.output_id = o.output_id AND c.block_index = 0
             WHERE j.report_job_id = @ReportJobId
            """,
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>Reads the object a job published, through the verifying storage reader.</summary>
    /// <param name="row">The job's row, carrying the artifact's key, size and hash.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<byte[]> ReadArtifactAsync(JobStateRow row, CancellationToken cancellationToken)
    {
        Assert.NotNull(row.ObjectKey);
        var read = await Objects.OpenReadAsync(
            ObjectKey.Create(row.ObjectKey!),
            row.SizeBytes!.Value,
            row.Sha256!,
            cancellationToken);
        Assert.True(read.Succeeded, $"the published object of {row.ArtifactId} had to verify");
        await using var content = read.Value!;
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
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
            await _engine.DisposeAsync();
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

    /// <summary>The sample template's own document, with its items repeated to <paramref name="records"/>.</summary>
    /// <param name="records">How many records the snapshot holds.</param>
    /// <param name="tallDescription">Whether one record carries the long description the tall-row case prints.</param>
    public static byte[] Snapshot(int records, bool tallDescription = false)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            if (tallDescription && index == 11)
            {
                item["description"] = new string('t', 160 * 1024);
            }

            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    /// <summary>The sample template's header with no record at all: the empty report.</summary>
    public static byte[] EmptySnapshot()
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        sample["items"] = new JsonArray();
        sample["header"]!["totals"]!["recordCount"] = 0;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private async Task OpenAsync(bool startOrchestration, CancellationToken cancellationToken)
    {
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
        Jobs = new PostgresJobStore(_dataSource);
        Objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StoragePath(), CreateIfMissing = true },
            StoragePath(),
            TimeProvider.System,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>());

        await StartEngineAsync(cancellationToken);

        Source = new GitHubTemplateSourceStub();
        SeedSource(Source);
        _source = await TemplateSourceHost.StartAsync(Source, cancellationToken);

        _api = Api();
        _pdf = Pdf();
        _orchestration = startOrchestration ? Orchestration() : null;
        await WaitForHealthyAsync(_api.CreateClient(), ReadyDeadline, cancellationToken);
        await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);
        if (_orchestration is not null)
        {
            await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
        }
    }

    // The four versions of the sample template: the release, one later release that prints its
    // own version in its header, one that declares no field it prints as it is, and one that
    // declares page subtotals.
    private static void SeedSource(GitHubTemplateSourceStub stub)
    {
        foreach (var version in new[] { First, Second })
        {
            stub.SeedPackage(Template, version, Package(version, declaresAnchors: true, pageTotals: false));
        }

        stub.SeedPackage(Template, NoAnchors, Package(NoAnchors, declaresAnchors: false, pageTotals: false));
        stub.SeedPackage(Template, PageTotals, Package(PageTotals, declaresAnchors: true, pageTotals: true));
    }

    private static Dictionary<string, byte[]> Package(string version, bool declaresAnchors, bool pageTotals)
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Template);
        var package = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "sample.json", StringComparison.Ordinal))
            {
                continue;
            }

            package[relative] = File.ReadAllBytes(file);
        }

        var manifest = Encoding.UTF8.GetString(package["template.yaml"])
            .Replace("version: \"" + First + "\"", "version: \"" + version + "\"", StringComparison.Ordinal)
            .Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
        if (!declaresAnchors)
        {
            manifest = RemoveTheVerificationBlock(manifest);
        }

        if (pageTotals)
        {
            manifest = manifest.Replace("enabled: false", "enabled: true", StringComparison.Ordinal);
        }

        package["template.yaml"] = Encoding.UTF8.GetBytes(manifest);

        // The package prints with one named font family, so every document of these cases is
        // laid out with the font the image carries instead of whatever the browser picks.
        var styles = Encoding.UTF8.GetString(package["html/styles.css"])
            .Replace("font-family: sans-serif;", "font-family: \"Liberation Sans\";", StringComparison.Ordinal);
        package["html/styles.css"] = Encoding.UTF8.GetBytes(styles);

        // The document is served at the package root, so its stylesheet is named from there:
        // the sample's parts name it relative to their own folder, which the print would ask
        // for at a path the package does not hold.
        var startWithStyles = Encoding.UTF8.GetString(package["html/document-start.html.sbn"])
            .Replace("href=\"styles.css\"", "href=\"html/styles.css\"", StringComparison.Ordinal);
        package["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes(startWithStyles);

        // The document names the version it was printed from, so a reading of the PDF says which
        // version a job printed without reading the job's pin.
        var start = Encoding.UTF8.GetString(package["html/document-start.html.sbn"])
            .Replace(
                "<h1>{{ header.reportTitle | html.escape }}</h1>",
                "<h1>{{ header.reportTitle | html.escape }}</h1>\n  <p class=\"template-version\">" + Template + " " + version + "</p>",
                StringComparison.Ordinal);
        package["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes(start);
        return package;
    }

    private static string RemoveTheVerificationBlock(string manifest)
    {
        var lines = manifest.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var start = lines.FindIndex(line => line.TrimStart().StartsWith("verification:", StringComparison.Ordinal));
        Assert.True(start >= 0, "the sample manifest declares the fields it prints as they are");
        var indentation = lines[start].Length - lines[start].TrimStart().Length;
        var end = start + 1;
        while (end < lines.Count
               && (lines[end].Length - lines[end].TrimStart().Length) > indentation)
        {
            end++;
        }

        lines.RemoveRange(start, end - start);
        return string.Join('\n', lines);
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        _engine = ContainerSmoke.Builder(_engineImage, "temporal-print")
            .WithCommand("server", "start-dev", "--ip", "0.0.0.0")
            .WithPortBinding(_hostPort, GrpcPort)
            .Build();
        await ContainerSmoke.StartAsync(_engine, _engineImage, cancellationToken);
        await Wait.UntilAsync(
            $"Temporal gRPC ({_engineImage.Reference}) on port {_hostPort}",
            async token =>
            {
                var probe = await TemporalClient.ConnectAsync(
                    new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" });
                return await probe.Connection.CheckHealthAsync(options: new RpcOptions { CancellationToken = token });
            },
            EngineStartDeadline,
            TimeSpan.FromSeconds(1),
            cancellationToken);
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(registry, "{\"consumers\":[{\"consumerId\":\"" + Consumer + "\",\"active\":true}]}");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", StoragePath());
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
            builder.UseSetting("Storage:Filesystem:Root", StoragePath());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
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
            builder.UseSetting("Storage:Filesystem:Root", StoragePath());
            builder.UseSetting("Storage:Filesystem:CreateIfMissing", "true");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:ApiBaseUrl", _source!.BaseUrl.AbsoluteUri);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Repository", GitHubTemplateSourceStub.Owner + "/" + GitHubTemplateSourceStub.Repository);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:Branch", GitHubTemplateSourceStub.Branch);
            builder.UseSetting($"{TemplateSourceOptions.SectionPath}:AllowInsecureLoopback", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private string StoragePath() => Path.Combine(_directory, "storage");

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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>One job as the composition's database records it after the run.</summary>
internal sealed class JobStateRow
{
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? ErrorReason { get; set; }

    public string? OutputStatus { get; set; }

    public string? OutputErrorCode { get; set; }

    public string? ArtifactId { get; set; }

    public string? ObjectKey { get; set; }

    public string? Sha256 { get; set; }

    public long? SizeBytes { get; set; }

    public int? PageCount { get; set; }

    public int? BlockCount { get; set; }

    public long? RecordCount { get; set; }

    public string? TemplatePackageSha256 { get; set; }

    public int? PartPages { get; set; }
}
