using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The demo of the printing path, over the whole local composition: a snapshot uploaded and confirmed
/// against the running API, a job accepted, and the job's own PDF printed from the template
/// version it was fixed to and published once, checked by the composition's records and by
/// reading the artifact back through the storage port.
/// <para>
/// The version the job prints is taken before the composition serves the job — the same state
/// a second job over an already-taken version is in — because the composition's render worker
/// is a container and the template source is outside it. The class therefore never needs the
/// source, and the version it seeds is the sample template exactly as the rendering path
/// reads it. The demo PDF is copied into the run's evidence with its SHA-256.
/// </para>
/// </summary>
public sealed class PrintSmallReportEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Template = "pilot-simple";

    private const string Version = "1.0.0";

    private const string SchemaPath = "schema.json";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan PublishedDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-print",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public PrintSmallReportEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task ASubmittedJobPrintsOnePdfAndPublishesIt()
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
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<FilesystemObjectStore>());
        var uploads = new PostgresUploadStore(dataSource);
        var versions = new PostgresTemplateVersionStore(dataSource);
        using var api = composition.CreateHttpClient("api");

        var snapshot = Snapshot(25);
        var uploadId = await ReadyUploadAsync(uploads, storage, snapshot, cancellationToken);
        await TakenAsync(dataSource, versions, storage, cancellationToken);

        var reportJobId = await SubmitAsync(api, uploadId, cancellationToken);
        var job = await WaitForSucceededAsync(dataSource, reportJobId, cancellationToken);

        // One artifact, whose object verifies by size and SHA-256 through the storage port.
        Assert.NotNull(job.ArtifactId);
        Assert.Equal("SUCCEEDED", job.Status);
        var read = await storage.OpenReadAsync(
            ObjectKey.Create(job.ObjectKey!),
            job.SizeBytes!.Value,
            job.Sha256!,
            cancellationToken);
        Assert.True(read.Succeeded, $"the artifact of {reportJobId} had to verify through the storage port");
        using var buffer = new MemoryStream();
        await using (var content = read.Value!)
        {
            await content.Content.CopyToAsync(buffer, cancellationToken);
        }

        var bytes = buffer.ToArray();
        Assert.Equal(job.SizeBytes, bytes.LongLength);
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        // The demo PDF stays with the run: pinned-simple, 25 records, the hash of the bytes a
        // consumer downloads.
        var path = RunEnvironment.WriteEvidence("pilot-simple-demo.pdf", bytes);
        var hash = RunEnvironment.Sha256Of(bytes);
        RunEnvironment.WriteEvidence(
            "pilot-simple-demo.evidence.txt",
            Encoding.UTF8.GetBytes($"pilot-simple-demo.pdf sha256={hash} bytes={bytes.LongLength}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"demo: {path} sha256={hash} bytes={bytes.LongLength}");
    }

    private static async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(Path.GetTempPath(), "report-service-e2e-print", "consumers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await builder.BuildAsync(cancellationToken);
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

    // The version the job prints, taken here in the test process with the product's own take:
    // the client dials the loopback double this process serves, and the take writes the
    // version's bytes to the composition's shared storage root and its record to the
    // composition's metadata database, so every process of the composition sees the taken
    // copy. The render worker container never reads a template source.
    private static async Task TakenAsync(
        NpgsqlDataSource dataSource,
        PostgresTemplateVersionStore versions,
        IObjectStore storage,
        CancellationToken cancellationToken)
    {
        var stub = new GitHubTemplateSourceStub();
        stub.SeedPackage(Template, Version, PackageFiles());
        await using var source = await TemplateSourceHost.StartAsync(stub, cancellationToken);

        var options = source.Options();
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var take = new TemplateTake(
            new GitHubTemplateSource(client, options),
            versions,
            storage,
            new TemplateManifestReader(),
            new TemplateVersionConflict(NullLogger<TemplateVersionConflict>.Instance),
            [],
            new TemplateTakeMetrics(),
            TimeProvider.System,
            Options.Create(options),
            NullLogger<TemplateTake>.Instance);

        var outcome = await take.TakeAsync(Template, Version, Consumer, commit: null, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(Version, outcome.Record!.Version);
        Assert.Equal(Consumer, outcome.Record.OwnerConsumerId);
    }

    // The sample template's own package, with the three corrections the rendering path needs:
    // the stylesheet named from the package root the document is served at, one named font
    // family, and the version printed in the header.
    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Template);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "sample.json", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
            }

            if (string.Equals(relative, "html/styles.css", StringComparison.Ordinal))
            {
                text = text.Replace("font-family: sans-serif;", "font-family: \"Liberation Sans\";", StringComparison.Ordinal);
            }

            if (string.Equals(relative, "html/document-start.html.sbn", StringComparison.Ordinal))
            {
                text = text
                    .Replace("href=\"styles.css\"", "href=\"html/styles.css\"", StringComparison.Ordinal)
                    .Replace(
                        "<h1>{{ header.reportTitle | html.escape }}</h1>",
                        "<h1>{{ header.reportTitle | html.escape }}</h1>\n  <p class=\"template-version\">" + Template + " " + Version + "</p>",
                        StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }


    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private static async Task<string> SubmitAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + Template + "\",\"version\":\"" + Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[\"pdf\"]}",
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

    private static async Task<PublishedJob> WaitForSucceededAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        PublishedJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to be published",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT j.status, a.artifact_id, a.object_key, a.sha256, a.size_bytes
                      FROM report_service.report_jobs j
                      LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
                      LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                     WHERE j.report_job_id = @id
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    job = new PublishedJob(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4));
                }

                return job is { Status: "SUCCEEDED" or "FAILED" or "CANCELLED" };
            },
            PublishedDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return job!;
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record PublishedJob(string Status, string? ArtifactId, string? ObjectKey, string? Sha256, long? SizeBytes);
}
