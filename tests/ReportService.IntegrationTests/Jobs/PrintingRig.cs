extern alias orchestration;
extern alias pdf;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Manifest;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;
using ReportJobWorkflow = orchestration::ReportService.Workers.Orchestration.Jobs.ReportJobWorkflow;
using PdfRendering = pdf::ReportService.Workers.Pdf.Rendering;
using PdfWorker = pdf::ReportService.Workers.Pdf.PdfWorkerProgram;
using Temporalio.Client;
using Temporalio.Common;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// One running composition of the three processes a report job needs, against real
/// dependencies: a PostgreSQL 18.6 database brought to the expected version by the product's
/// migrations, a Temporal 1.9.1 engine, one storage root, and the product's own API,
/// orchestration worker and render worker hosted on their real registrations. The template
/// source is the deployment's object store - the composition's own root, read through the
/// source class - seeded with the sample template at two versions and with the two versions a
/// job must refuse.
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

    /// <summary>The consumer the registry carries as inactive.</summary>
    public const string InactiveConsumer = "consumer-c";

    /// <summary>The sample template every case prints.</summary>
    public const string Template = "pilot-simple";

    /// <summary>The one report printed in three engines and under three settings.</summary>
    public const string SameReport = "same-report";

    /// <summary>The three versions of the one report, one engine each.</summary>
    public static IReadOnlyList<string> SameReportEngines { get; } = ["1.0.0", "1.1.0", "1.2.0"];

    /// <summary>The three settings the one report is printed under: the base locale and two others.</summary>
    public static IReadOnlyList<(string Culture, string TimeZone)> SameReportLocales { get; } =
    [
        ("es-AR", "America/Argentina/Buenos_Aires"),
        ("en-US", "Asia/Tokyo"),
        ("en-GB", "Europe/Madrid"),
    ];

    /// <summary>The version the sample template ships as the first release.</summary>
    public const string First = "1.0.0";

    /// <summary>A later version of the same template, taken while a job is pinned to the first.</summary>
    public const string Second = "1.1.0";

    /// <summary>The version whose manifest declares no field it prints as it is.</summary>
    public const string NoAnchors = "1.2.0";

    /// <summary>The version whose manifest declares page subtotals.</summary>
    public const string PageTotals = "1.3.0";

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(2);

    private readonly PostgresFixture _fixture;

    private readonly PinnedImage _engineImage = CompositionPins.Load().WorkflowEngine;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "rs12",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    private readonly LogCapture _logs = new();

    private readonly Dictionary<string, byte[]> _snapshotOf = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _uploadOf = new(StringComparer.Ordinal);

    private EngineStart.Engine? _engine;

    private int _hostPort;

    private SourceClassStore? _sourceStore;

    private string? _connectionString;

    private NpgsqlDataSource? _dataSource;

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<OrchestrationWorker>? _orchestration;

    private WebApplicationFactory<PdfWorker>? _pdf;


    private PrintingRig(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>Whether the render worker starts with the composition.</summary>
    private bool StartPdf { get; init; }

    /// <summary>The record count the orchestration worker applies, when the case states one.</summary>
    private int? SingleBlockRecordLimit { get; init; }

    /// <summary>The clock the API reads, when the case replaces it.</summary>
    private ManualTimeProvider? Clock { get; init; }

    /// <summary>The source class every take and resolution of these cases reads, publishing into the composition's own root.</summary>
    public TemplateSourceClass Source { get; private set; } = null!;

    /// <summary>Every operation the template source class performed, in order.</summary>
    public IReadOnlyList<SourceClassStore.SourceOperation> SourceOperations => _sourceStore!.SourceOperations;

    /// <summary>The storage the composition writes to.</summary>
    public IObjectStore Objects { get; private set; } = null!;

    /// <summary>The job records the composition writes.</summary>
    public IJobStore Jobs { get; private set; } = null!;

    /// <summary>The database the composition uses.</summary>
    public NpgsqlDataSource DataSource => _dataSource!;

    /// <summary>The connection string the composition's database was opened on.</summary>
    public string ConnectionString => _connectionString!;

    /// <summary>Every line the composition's processes logged.</summary>
    public LogCapture Logs => _logs;

    /// <summary>The root the composition's storage keeps its objects under.</summary>
    public string StorageRoot => Path.Combine(_directory, "storage");

    /// <summary>Starts the composition on the case's own database and engine.</summary>
    /// <param name="fixture">The PostgreSQL container the class owns.</param>
    /// <param name="startOrchestration">Whether the job's own worker starts now; a case that pins two jobs starts it later.</param>
    /// <param name="startPdf">Whether the render worker starts with the composition.</param>
    /// <param name="clock">The clock the API reads, when the case replaces it.</param>
    /// <param name="singleBlockRecordLimit">
    /// The record count the orchestration worker applies; the deployment's default when the case states none.
    /// The setting is read when the worker starts, so a case that asserts a limit starts its own composition.
    /// </param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task<PrintingRig> StartAsync(
        PostgresFixture fixture,
        bool startOrchestration = true,
        bool startPdf = true,
        ManualTimeProvider? clock = null,
        int? singleBlockRecordLimit = null,
        CancellationToken cancellationToken = default)
    {
        var rig = new PrintingRig(fixture)
        {
            StartPdf = startPdf,
            Clock = clock,
            SingleBlockRecordLimit = singleBlockRecordLimit,
        };
        try
        {
            await rig.OpenAsync(startOrchestration, cancellationToken);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }

        return rig;
    }

    /// <summary>Starts the orchestration worker, which serves the job's execution and its data step.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StartOrchestrationAsync(CancellationToken cancellationToken)
    {
        _orchestration ??= Orchestration();
        await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
    }

    /// <summary>Starts the render worker, which serves the step that prints a job's block.</summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task StartPdfAsync(CancellationToken cancellationToken)
    {
        _pdf ??= Pdf();
        await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);
    }

    /// <summary>Stops answering the template source, as an outage of the deployment's store does.</summary>
    public void StopTheSource() => _sourceStore!.Fail = true;

    /// <summary>Creates a ready upload of <paramref name="document"/> and answers its identifier.</summary>
    /// <param name="document">The snapshot's bytes.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="consumer">The consumer the upload belongs to.</param>
    public Task<string> ReadyUploadAsync(byte[] document, CancellationToken cancellationToken, string consumer = Consumer) =>
        ReadyUploadAsync(document, UploadContentEncoding.None, cancellationToken, consumer);

    /// <summary>
    /// Creates a ready upload of <paramref name="document"/>, stored with
    /// <paramref name="encoding"/>: a gzip-stored snapshot is written compressed, and its record
    /// carries the uncompressed document's size and SHA-256 beside the stored bytes' own.
    /// </summary>
    /// <param name="document">The snapshot's bytes.</param>
    /// <param name="encoding">How the bytes are stored.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="consumer">The consumer the upload belongs to.</param>
    public async Task<string> ReadyUploadAsync(
        byte[] document,
        UploadContentEncoding encoding,
        CancellationToken cancellationToken,
        string consumer = Consumer)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var uploads = new PostgresUploadStore(_dataSource!);
        await uploads.CreateAsync(
            new UploadRecord(
                uploadId,
                consumer,
                UploadMode.ApiStream,
                DataProfile.HeaderItemsV1,
                UploadStatus.Created,
                ObjectKey.FromSegments("staging", uploadId, "content"),
                now,
                now.AddHours(1)),
            cancellationToken);

        var stored = document;
        if (encoding == UploadContentEncoding.Gzip)
        {
            using var compressed = new MemoryStream();
            await using (var gzip = new System.IO.Compression.GZipStream(
                compressed,
                System.IO.Compression.CompressionLevel.SmallestSize,
                leaveOpen: true))
            {
                await gzip.WriteAsync(document, cancellationToken);
            }

            stored = compressed.ToArray();
        }

        var documentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(document));
        var storedSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored));
        Assert.True(await uploads.TryStartTransferAsync(uploadId, consumer, encoding, now, cancellationToken));
        Assert.True(await uploads.RecordTransferAsync(
            uploadId,
            UploadContentOutcome.Complete,
            document.LongLength,
            stored.LongLength,
            storedSha256,
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, consumer, null, now, now, now.AddHours(1), cancellationToken));

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(stored, writable: false);
        var written = await Objects.WriteAsync(key, content, cancellationToken);
        Assert.True(written.Succeeded);
        _snapshotOf[uploadId] = document;
        Assert.Equal(
            SnapshotCommitResult.Committed,
            await uploads.CommitReadyAsync(
                new SnapshotRecord(
                    uploadId,
                    consumer,
                    key,
                    encoding,
                    DataProfile.HeaderItemsV1,
                    documentSha256,
                    document.LongLength,
                    storedSha256,
                    stored.LongLength,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    /// <summary>
    /// Records <paramref name="version"/> as a version already taken, with its package stored at
    /// the content-addressed key of its own bytes and the manifest the package's files carry.
    /// </summary>
    /// <param name="version">The version to record.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task SeedTakenVersionAsync(string version, CancellationToken cancellationToken)
    {
        await RecordTakenAsync(Template, version, Package(version, declaresAnchors: false, pageTotals: false), cancellationToken);
    }

    /// <summary>
    /// Records the three versions of the one report as taken versions of its own template, each
    /// with the package folder it ships as: the same report in three syntaxes, one engine each.
    /// </summary>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task SeedSameReportAsync(CancellationToken cancellationToken)
    {
        foreach (var version in SameReportEngines)
        {
            var folder = Path.Combine(
                RepositoryPaths.Root,
                "tests",
                "ReportService.Testing",
                "TemplatePackages",
                SameReport,
                version);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
                files[relative] = File.ReadAllBytes(file);
            }

            await RecordTakenAsync(SameReport, version, files, cancellationToken);
        }
    }

    private async Task RecordTakenAsync(string templateId, string version, Dictionary<string, byte[]> files, CancellationToken cancellationToken)
    {
        var sources = files
            .Select(file => new PackageFileSource(
                file.Key,
                _ => Task.FromResult<Stream>(new MemoryStream(file.Value, writable: false))))
            .ToList();

        using var archive = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(sources, archive, cancellationToken);
        var bytes = archive.ToArray();
        var packageSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(Consumer, templateId, version, packageSha256);
        using (var content = new MemoryStream(bytes, writable: false))
        {
            var written = await Objects.WriteAsync(key, content, cancellationToken);
            Assert.True(
                written.Succeeded || written.Outcome == StorageOutcome.AlreadyExists,
                $"the taken package had to be written: the store answered {written.Outcome} "
                + $"({written.Error?.Code ?? "no code"}: {written.Error?.Message ?? "no message"})");
        }

        // The record carries the manifest the archived files themselves declare, so the taken
        // copy and its record cannot describe two different packages.
        var read = new TemplateManifestReader().Read(new MemoryStream(files["template.yaml"], writable: false));
        Assert.True(read.Succeeded, read.Reason);
        var manifest = read.Manifest!;
        var record = TemplateVersionRecords.Sample(
                templateId,
                version,
                manifest.Owner ?? Consumer,
                shared: manifest.Shared,
                schemaRef: manifest.DataContract!.Schema!,
                packageSha256: packageSha256) with
            {
                ManifestVersion = manifest.ManifestVersion!,
                Engine = manifest.Engine!.Value,
                EngineVersion = TemplateEngineCatalog.VersionOf(manifest.Engine.Value),
                PackageSize = bytes.LongLength,
                FileCount = files.Count,
                ObjectKey = key,
                Manifest = manifest,
                ManifestJson = read.CanonicalJson!,
            };
        var inserted = await new PostgresTemplateVersionStore(_dataSource!).TryInsertAsync(record, cancellationToken);
        Assert.True(
            inserted.WasInserted,
            $"the taken version had to be recorded once: the store answered with version {inserted.Record.Version} of {inserted.Record.TemplateId}");
    }

    /// <summary>The snapshot one job was printed from, as the case handed it to the upload.</summary>
    /// <param name="reportJobId">The job.</param>
    public byte[] SnapshotOf(string reportJobId) => _snapshotOf[_uploadOf[reportJobId]];

    /// <summary>The client a case sends its own requests with, as the composition's own API serves them.</summary>
    public HttpClient CreateApiClient() => _api!.CreateClient();

    /// <summary>Submits one job through the product's own API and answers its identifier.</summary>
    /// <param name="uploadId">The ready upload it reports on.</param>
    /// <param name="version">The exact version it names.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public Task<string> SubmitAsync(string uploadId, string? version, CancellationToken cancellationToken, string consumer = Consumer) =>
        SubmitAsync(uploadId, Template, version, culture: null, timeZone: null, cancellationToken, consumer);

    /// <summary>
    /// Submits one job of <paramref name="templateId"/> at <paramref name="version"/>, naming the
    /// culture and the time zone the report is presented in when the case gives them, and
    /// answers its identifier.
    /// </summary>
    /// <param name="uploadId">The ready upload it reports on.</param>
    /// <param name="templateId">The template the job takes its version of.</param>
    /// <param name="version">The exact version the job names, or null to take the latest.</param>
    /// <param name="culture">The culture the report is written in, or null to take the package's own.</param>
    /// <param name="timeZone">The time zone the report is read as, or null to take the package's own.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    /// <param name="consumer">The consumer the job is submitted for.</param>
    public async Task<string> SubmitAsync(
        string uploadId,
        string templateId,
        string? version,
        string? culture,
        string? timeZone,
        CancellationToken cancellationToken,
        string consumer = Consumer)
    {
        using var api = _api!.CreateClient();
        var template = version is null
            ? "{\"id\":\"" + templateId + "\"}"
            : "{\"id\":\"" + templateId + "\",\"version\":\"" + version + "\"}";
        var render = culture is null && timeZone is null
            ? string.Empty
            : ",\"render\":{" + (culture is null ? string.Empty : "\"culture\":\"" + culture + "\"")
                + (culture is not null && timeZone is not null ? "," : string.Empty)
                + (timeZone is null ? string.Empty : "\"timeZone\":\"" + timeZone + "\"") + "}";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":" + template + ",\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[\"pdf\"]" + render + "}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        var reportJobId = document.RootElement.GetProperty("reportJobId").GetString()!;
        _uploadOf[reportJobId] = uploadId;
        return reportJobId;
    }

    /// <summary>
    /// Installs the fault that makes one block's first attempt fail after it printed, so the
    /// engine retries it: the seam exists on the render step only, and this host is the only
    /// place that installs it.
    /// </summary>
    /// <param name="fault">The fault the case wants.</param>
    public void InstallPartCommitFault(PdfRendering.PartCommitFault fault)
    {
        Assert.NotNull(_pdf);
        _pdf!.Services.GetRequiredService<PdfRendering.RenderPdfBlockActivity>().PartCommitFault = fault;
    }

    /// <summary>The keys this run wrote under one job's output namespace, as the storage root holds them.</summary>
    /// <param name="reportJobId">The job.</param>
    /// <param name="outputId">The output.</param>
    public IReadOnlyList<string> OutputObjectKeys(string reportJobId, string outputId)
    {
        var root = Path.Combine(StorageRoot, "outputs", reportJobId, outputId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => "outputs/" + Path.GetRelativePath(Path.Combine(StorageRoot, "outputs"), file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Reads one object of the composition's storage through the verifying reader.</summary>
    /// <param name="key">The key the object was written under.</param>
    /// <param name="size">The size the record declares.</param>
    /// <param name="sha256">The SHA-256 the record declares.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<byte[]> ReadObjectAsync(string key, long size, string sha256, CancellationToken cancellationToken)
    {
        var read = await Objects.OpenReadAsync(ObjectKey.Create(key), size, sha256, cancellationToken);
        Assert.True(read.Succeeded, $"the object {key} had to verify ({read.Outcome})");
        await using var content = read.Value!;
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads the bytes the storage root holds for <paramref name="key"/> as they are: the
    /// reading of an object no record declares - the orphan of an attempt that committed
    /// nothing - which therefore has no size and no hash to verify it against.
    /// </summary>
    /// <param name="key">The key the object was written under.</param>
    public byte[] ReadStoredObject(string key) =>
        File.ReadAllBytes(Path.Combine(StorageRoot, key.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The instant the job was accepted with, as the composition recorded it.</summary>
    /// <param name="reportJobId">The job.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<DateTimeOffset> ReportClockAsync(string reportJobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<DateTimeOffset>(new CommandDefinition(
            "SELECT report_clock FROM report_service.report_jobs WHERE report_job_id = @ReportJobId",
            new { ReportJobId = reportJobId },
            cancellationToken: cancellationToken));
    }

    /// <summary>How many blocks of one output the composition committed.</summary>
    /// <param name="outputId">The output.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<long> CountPartCommitsAsync(string outputId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM report_service.pdf_part_commits WHERE output_id = @OutputId",
            new { OutputId = outputId },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Records one more version of the one report, declaring another engine version: the same
    /// package the base version ships, with its manifest naming the version and the engine
    /// version the case needs. A deployment refuses it before composing, so no document exists.
    /// </summary>
    /// <param name="version">The version to record.</param>
    /// <param name="engineVersion">The engine version the manifest declares.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task SeedSameReportVersionWithEngineVersionAsync(string version, string engineVersion, CancellationToken cancellationToken)
    {
        var source = Path.Combine(
            RepositoryPaths.Root,
            "tests",
            "ReportService.Testing",
            "TemplatePackages",
            SameReport,
            SameReportEngines[0]);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            files[relative] = File.ReadAllBytes(file);
        }

        var manifest = Encoding.UTF8.GetString(files["template.yaml"])
            .Replace("version: \"1.0.0\"", "version: \"" + version + "\"", StringComparison.Ordinal)
            .Replace("engineVersion: \"7.5.1\"", "engineVersion: \"" + engineVersion + "\"", StringComparison.Ordinal);
        files["template.yaml"] = Encoding.UTF8.GetBytes(manifest);
        await RecordTakenAsync(SameReport, version, files, cancellationToken);
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
                   o.output_id AS "OutputId",
                   o.status AS "OutputStatus", o.error_code AS "OutputErrorCode", o.error_reason AS "OutputErrorReason",
                   a.artifact_id AS "ArtifactId", a.object_key AS "ObjectKey",
                   a.sha256 AS "Sha256", a.size_bytes AS "SizeBytes", a.page_count AS "PageCount", a.expires_at AS "ExpiresAt",
                   a.created_at AS "ArtifactCreatedAt",
                   p.block_count AS "BlockCount", p.record_count AS "RecordCount",
                   c.template_package_sha256 AS "TemplatePackageSha256", c.page_count AS "PartPages",
                   c.object_key AS "PartObjectKey", c.presentation::text AS "Presentation"
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

    /// <summary>The run's history, as the composition's own engine recorded it.</summary>
    /// <param name="reportJobId">The job whose run is read.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public async Task<WorkflowHistory> HistoryAsync(string reportJobId, CancellationToken cancellationToken)
    {
        var client = await TemporalClient
            .ConnectAsync(new TemporalClientConnectOptions($"127.0.0.1:{_hostPort}") { Namespace = "default" })
            .WaitAsync(cancellationToken);
        return await client
            .GetWorkflowHandle(ReportJobWorkflow.WorkflowIdFor(reportJobId))
            .FetchHistoryAsync(new WorkflowHistoryEventFetchOptions())
            .WaitAsync(cancellationToken);
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

        _sourceStore = new SourceClassStore(FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = SourcePath(), CreateIfMissing = true },
            SourcePath(),
            TimeProvider.System,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>()));
        Source = TemplateSourceClass.Over(_sourceStore);
        await SeedSourceAsync(Source);

        _api = Api();
        _pdf = StartPdf ? Pdf() : null;
        _orchestration = startOrchestration ? Orchestration() : null;
        await WaitForHealthyAsync(_api.CreateClient(), ReadyDeadline, cancellationToken);
        if (_pdf is not null)
        {
            await WaitForHealthyAsync(_pdf.CreateClient(), ReadyDeadline, cancellationToken);
        }

        if (_orchestration is not null)
        {
            await WaitForHealthyAsync(_orchestration.CreateClient(), ReadyDeadline, cancellationToken);
        }
    }

    // The four versions of the sample template: the release, one later release that prints its
    // own version in its header, one that declares no field it prints as it is, and one that
    // declares page subtotals.
    private static async Task SeedSourceAsync(TemplateSourceClass source)
    {
        foreach (var version in new[] { First, Second })
        {
            await source.PublishAsync(Template, version, Package(version, declaresAnchors: true, pageTotals: false));
        }

        await source.PublishAsync(Template, NoAnchors, Package(NoAnchors, declaresAnchors: false, pageTotals: false));
        await source.PublishAsync(Template, PageTotals, Package(PageTotals, declaresAnchors: true, pageTotals: true));
    }

    private static Dictionary<string, byte[]> Package(string version, bool declaresAnchors, bool pageTotals)
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Template);
        var package = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
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
        _engine = await EngineStart.StartAsync(_engineImage, "temporal-print", cancellationToken);
        _hostPort = _engine.HostPort;
    }

    private WebApplicationFactory<Program> Api()
    {
        var registry = Path.Combine(_directory, "consumers.json");
        File.WriteAllText(
            registry,
            "{\"consumers\":[{\"consumerId\":\"" + Consumer + "\",\"active\":true},"
            + "{\"consumerId\":\"" + InactiveConsumer + "\",\"active\":false}]}");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", StoragePath());
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            if (Clock is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                });
            }
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
            builder.UseSetting("Retention:Outputs", "01:00:00");
            if (SingleBlockRecordLimit is { } limit)
            {
                builder.UseSetting(
                    "Partitioning:SingleBlockRecordLimit",
                    limit.ToString(CultureInfo.InvariantCulture));
            }

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
            builder.UseSetting("TemplateSource:Store:Filesystem:Root", SourcePath());
            builder.UseSetting("TemplateSource:Store:Filesystem:CreateIfMissing", "false");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, _connectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MigrationCatalog.ExpectedVersion);
            builder.ConfigureTestServices(services =>
            {
                // The source reads through its own instance, so the class's spy is registered
                // under the source's key: the writable store stays the deployment's own.
                services.RemoveAllKeyed<IObjectStore>(TemplateSourceStorage.StoreServiceKey);
                services.AddKeyedSingleton<IObjectStore>(TemplateSourceStorage.StoreServiceKey, _sourceStore!);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });

    private string StoragePath() => Path.Combine(_directory, "storage");

    private string SourcePath() => Path.Combine(_directory, "template-source");

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
}

/// <summary>One job as the composition's database records it after the run.</summary>
internal sealed class JobStateRow
{
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? ErrorReason { get; set; }

    public string? OutputId { get; set; }

    public string? OutputStatus { get; set; }

    public string? OutputErrorCode { get; set; }

    public string? OutputErrorReason { get; set; }

    public string? ArtifactId { get; set; }

    public string? ObjectKey { get; set; }

    public string? Sha256 { get; set; }

    public long? SizeBytes { get; set; }

    public int? PageCount { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? ArtifactCreatedAt { get; set; }

    public int? BlockCount { get; set; }

    public long? RecordCount { get; set; }

    public string? TemplatePackageSha256 { get; set; }

    public int? PartPages { get; set; }

    /// <summary>The key the committed part names: the object the published artifact is built from.</summary>
    public string? PartObjectKey { get; set; }

    /// <summary>The presentation provenance the committed part records, as the column stores it.</summary>
    public string? Presentation { get; set; }
}
