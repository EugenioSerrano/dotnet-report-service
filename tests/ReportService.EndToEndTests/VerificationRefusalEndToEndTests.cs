using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Testing.VerificationLoss;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The refusal over the whole local composition, followed the way a consumer follows its report:
/// a corpus snapshot whose anchored description holds a character the browser does not paint is
/// uploaded and confirmed against the running API, the job is accepted, and the job's own record
/// is polled until it ends. The print fails its own check, so the job and its PDF output end
/// failed with the refusal's code, no artifact and no part commit exist, and no object was
/// written under the output's prefix. The demo transcript of the refusal is kept with the run.
/// </summary>
public sealed class VerificationRefusalEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }
        """;

    /// <summary>The consumer the class submits as, and the owner of the version it prints.</summary>
    private const string Consumer = "consumer-a";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-refusal",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public VerificationRefusalEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose()
    {
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
    public async Task APrintThatLosesPartOfItsTextEndsTheJobFailedWithNothingToDownload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        var connectionString = await composition.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new PostgresUploadStore(dataSource);
        var versions = new PostgresTemplateVersionStore(dataSource);
        using var api = composition.CreateHttpClient("api");

        await TakenAsync(dataSource, versions, storage, cancellationToken);
        var uploadId = await ReadyUploadAsync(
            uploads,
            storage,
            VerificationLossCorpus.Snapshot(VerificationLossCorpus.TextLoss).Bytes,
            cancellationToken);
        var reportJobId = await SubmitAsync(api, uploadId, cancellationToken);
        var job = await WaitForTerminalAsync(dataSource, reportJobId, cancellationToken);

        // The consumer's answer: failed, with the code the verification refused the print with.
        Assert.Equal("FAILED", job.Status);
        Assert.Equal("PAGINATION_FAILED", job.ErrorCode);
        Assert.Equal("FAILED", job.OutputStatus);
        Assert.Equal("PAGINATION_FAILED", job.OutputErrorCode);

        // Nothing to download: no artifact row, no part commit and no object under the prefix.
        Assert.Null(job.ArtifactId);
        Assert.Equal(0L, await CountPartsAsync(dataSource, reportJobId, cancellationToken));
        Assert.Equal(0L, await CountArtifactsAsync(dataSource, reportJobId, cancellationToken));
        Assert.False(
            Directory.Exists(Path.Combine(StorageRoot(), "outputs", reportJobId, job.OutputId!)),
            "a refused print wrote an object under the output's prefix");

        KeepTranscript(reportJobId, job, cancellationToken);
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);

        var appHost = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        await Wait.UntilAsync(
            "the API answering /health",
            async token =>
            {
                using var response = await api.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == HttpStatusCode.OK;
            },
            HealthyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return app;
    }

    // The corpus version the class's job prints, taken here with the product's own take: the
    // package is published into the composition's own storage root under this consumer's name,
    // and the take writes the immutable copy and the record every process of the composition
    // reads. The package's own parts and samples are the corpus's, unchanged.
    private static async Task TakenAsync(
        NpgsqlDataSource dataSource,
        PostgresTemplateVersionStore versions,
        IObjectStore storage,
        CancellationToken cancellationToken)
    {
        var files = VerificationLossCorpus.PackageFiles(VerificationLossCorpus.Template)
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        files["template.yaml"] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(files["template.yaml"])
                .Replace("owner: " + VerificationLossCorpus.Consumer, "owner: " + Consumer, StringComparison.Ordinal));

        var source = TemplateSourceClass.Over(storage);
        await source.RemoveAsync(VerificationLossCorpus.Template, VerificationLossCorpus.Version);
        await source.PublishAsync(VerificationLossCorpus.Template, VerificationLossCorpus.Version, files);

        var options = new TemplateSourceOptions();
        var take = new TemplateTake(
            new StorageTemplateSource(storage, options, new TemplateSourceMetrics(), NullLogger<StorageTemplateSource>.Instance),
            versions,
            storage,
            new TemplateManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            [],
            new TemplateTakeMetrics(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<TemplateTake>.Instance);

        var outcome = await take.TakeAsync(VerificationLossCorpus.Template, VerificationLossCorpus.Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(VerificationLossCorpus.Version, outcome.Record!.Version);
    }

    // A ready upload of the snapshot, written through the composition's own records and storage.
    private static async Task<string> ReadyUploadAsync(
        PostgresUploadStore uploads,
        IObjectStore storage,
        byte[] snapshot,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uploadId = UploadIdentifier.Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
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
            snapshot.LongLength,
            snapshot.LongLength,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot)),
            now,
            cancellationToken));
        Assert.True(await uploads.ConfirmAsync(uploadId, Consumer, null, now, now, now.AddHours(1), cancellationToken));

        var key = ObjectKey.FromSegments("snapshots", uploadId, "a");
        using var content = new MemoryStream(snapshot, writable: false);
        var written = await storage.WriteAsync(key, content, cancellationToken);
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
                    snapshot.LongLength,
                    written.Value.Sha256,
                    written.Value.Size,
                    now),
                now,
                cancellationToken));
        return uploadId;
    }

    private static async Task<string> SubmitAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        using var accepted = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent(
                    $$"""{"template":{"id":"{{VerificationLossCorpus.Template}}","version":"{{VerificationLossCorpus.Version}}"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                    Encoding.UTF8,
                    "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        var body = await accepted.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)accepted.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private static async Task<RefusedJob> WaitForTerminalAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        RefusedJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to end",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT j.status, j.error_code, o.output_id, o.status, o.error_code, a.artifact_id
                      FROM report_service.report_jobs j
                      LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
                      LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                     WHERE j.report_job_id = @id
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    job = new RefusedJob(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5));
                }

                return job is { Status: "FAILED" or "SUCCEEDED" or "CANCELLED" };
            },
            TerminalDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return job!;
    }

    private static async Task<long> CountPartsAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken) =>
        await CountAsync(
            dataSource,
            "SELECT count(*) FROM report_service.pdf_part_commits c JOIN report_service.report_outputs o ON o.output_id = c.output_id WHERE o.report_job_id = @id",
            reportJobId,
            cancellationToken);

    private static async Task<long> CountArtifactsAsync(NpgsqlDataSource dataSource, string reportJobId, CancellationToken cancellationToken) =>
        await CountAsync(
            dataSource,
            "SELECT count(*) FROM report_service.artifacts WHERE report_job_id = @id",
            reportJobId,
            cancellationToken);

    private static async Task<long> CountAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    // The demo transcript: what the consumer is left with, as the composition recorded it, kept
    // with the run under its own SHA-256.
    private void KeepTranscript(string reportJobId, RefusedJob job, CancellationToken cancellationToken)
    {
        var transcript = JsonSerializer.Serialize(
            new
            {
                reportJobId,
                job = new { status = job.Status, errorCode = job.ErrorCode },
                output = new { outputId = job.OutputId, status = job.OutputStatus, errorCode = job.OutputErrorCode },
                artifactCount = 0,
                partCommitCount = 0,
                outputPrefix = $"outputs/{reportJobId}/{job.OutputId}/",
                objectsUnderThePrefix = 0,
            },
            new JsonSerializerOptions { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(transcript + "\n");
        var path = RunEnvironment.WriteEvidence("verification-refusal-demo.json", bytes);
        var hash = RunEnvironment.Sha256Of(bytes);
        RunEnvironment.WriteEvidence(
            "verification-refusal-demo.evidence.txt",
            Encoding.UTF8.GetBytes($"verification-refusal-demo.json sha256={hash} bytes={bytes.LongLength}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"transcript: {path} sha256={hash}");
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record RefusedJob(
        string Status,
        string? ErrorCode,
        string? OutputId,
        string? OutputStatus,
        string? OutputErrorCode,
        string? ArtifactId);
}
