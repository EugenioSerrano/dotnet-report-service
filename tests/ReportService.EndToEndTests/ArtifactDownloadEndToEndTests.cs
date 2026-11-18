using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The whole demonstration over the local composition, with the output retention shortened to a
/// minute: a snapshot uploaded and ready, a job accepted and printed, its artifacts listed, the
/// file downloaded and verified against the hash and the size the listing carries, and — once the
/// expiry the publication recorded has passed — the same download answering that the artifact has
/// expired while the job still reads succeeded. The transcript of this case is the record of the
/// demonstration.
/// </summary>
[Trait("Requires", "printing")]
public sealed class ArtifactDownloadEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Template = "pilot-simple-artifact";

    // The shipped sample the package is built from; the published identity is this class's own.
    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    /// <summary>The output retention the composition of this case runs with: one minute.</summary>
    private const string Retention = "00:01:00";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan PublishedDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-download",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public ArtifactDownloadEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task AJobIsListedAndDownloadedAndItsFileExpiresAfterTheConfiguredMinute()
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

        var uploadId = await ReadyUploadAsync(uploads, storage, Snapshot(25), cancellationToken);
        await TakenAsync(dataSource, versions, storage, cancellationToken);
        var reportJobId = await SubmitAsync(api, uploadId, cancellationToken);
        var status = await WaitForSucceededAsync(api, reportJobId, cancellationToken);
        Assert.Equal("succeeded", status.Status);
        Assert.Equal("succeeded", Assert.Single(status.Outputs).Status);

        // The listing: exactly one PDF, with the hash, the size and the expired-at instant.
        var listed = Assert.Single((await ListAsync(api, reportJobId, cancellationToken)).Artifacts);
        Assert.Equal("pdf", listed.Format);
        Assert.Equal("application/pdf", listed.MimeType);
        Assert.Equal(status.Outputs[0].ArtifactId, listed.ArtifactId);

        // The download: the bytes the listing promised, verified by the test.
        using (var response = await DownloadAsync(api, reportJobId, listed.ArtifactId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(listed.SizeBytes, response.Content.Headers.ContentLength);
            var installed = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(listed.SizeBytes, installed.LongLength);
            Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(installed)));
        }

        // The instant the publication recorded, in UTC with whole milliseconds: the retention the
        // composition ran with is the one between the artifact's creation and this instant.
        var now = DateTimeOffset.UtcNow;
        Assert.True(listed.ExpiresAt > now, $"the artifact's expiry {listed.ExpiresAt:O} has to be in the future at the download");
        Assert.True(
            listed.ExpiresAt - now <= TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(5),
            $"the composition ran with a one-minute retention, and this artifact expires {listed.ExpiresAt - now} from now");

        // Past the recorded instant, plus a second: the same download answers that it expired, and
        // the job is untouched.
        var wait = listed.ExpiresAt.AddSeconds(1) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }

        using (var expired = await DownloadAsync(api, reportJobId, listed.ArtifactId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            using var problem = JsonDocument.Parse(await expired.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(
                ErrorCodes.ArtifactExpired,
                problem.RootElement.GetProperty("code").GetString());
        }

        var after = await ReadStatusAsync(api, reportJobId, cancellationToken);
        Assert.Equal("succeeded", after.Status);
        Assert.Equal(status.Outputs[0].ArtifactId, Assert.Single(after.Outputs).ArtifactId);

        var transcript = string.Create(
            CultureInfo.InvariantCulture,
            $"report job {reportJobId}: listed 1 artifact {listed.ArtifactId} ({listed.SizeBytes} bytes, sha256 {listed.Sha256}); "
            + $"downloaded and verified; expiresAt {listed.ExpiresAt:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}; "
            + $"download after expiry: {(int)HttpStatusCode.Gone} ARTIFACT_EXPIRED; status afterwards: {after.Status}");
        TestContext.Current.TestOutputHelper?.WriteLine(transcript);
        RunEnvironment.WriteEvidence("artifact-download-demo.txt", Encoding.UTF8.GetBytes(transcript + Environment.NewLine));
    }

    private static async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(Path.GetTempPath(), "report-service-e2e-download", Guid.NewGuid().ToString("N") + "-consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        // The retention the composition runs with, stated once for the worker that records it.
        var builder = await TestComposition.CreateAsync(
            cancellationToken,
            $"--Consumers:File={registry}",
            $"--Retention:Outputs={Retention}");
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

    private static async Task<Listing> ListAsync(HttpClient api, string reportJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var artifacts = new List<ListedArtifact>();
        foreach (var artifact in document.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            artifacts.Add(new ListedArtifact(
                artifact.GetProperty("artifactId").GetString()!,
                artifact.GetProperty("format").GetString()!,
                artifact.GetProperty("mimeType").GetString()!,
                artifact.GetProperty("sizeBytes").GetInt64(),
                artifact.GetProperty("sha256").GetString()!,
                DateTimeOffset.ParseExact(
                    artifact.GetProperty("expiresAt").GetString()!,
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
        }

        return new Listing(document.RootElement.GetProperty("reportJobId").GetString()!, artifacts);
    }

    private static Task<HttpResponseMessage> DownloadAsync(
        HttpClient api,
        string reportJobId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<StatusAnswer> WaitForSucceededAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        StatusAnswer? answer = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to succeed",
            async token =>
            {
                answer = await ReadStatusAsync(api, reportJobId, token);
                return answer.Status is "succeeded" or "failed" or "cancelled";
            },
            PublishedDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return answer!;
    }

    private static async Task<StatusAnswer> ReadStatusAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new StatusAnswer(
            root.GetProperty("status").GetString()!,
            [
                .. root.GetProperty("outputs").EnumerateArray().Select(output => new OutputAnswer(
                    output.GetProperty("format").GetString()!,
                    output.GetProperty("status").GetString()!,
                    output.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null }
                        ? null
                        : output.GetProperty("artifactId").GetString())),
            ]);
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
            Convert.ToHexStringLower(SHA256.HashData(snapshot)),
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

    // The version the job prints, taken in the test process with the product's own take: the taken
    // bytes reach the composition's shared storage root and its record the metadata database, so
    // every process sees the taken copy.
    private static async Task TakenAsync(
        NpgsqlDataSource dataSource,
        PostgresTemplateVersionStore versions,
        IObjectStore storage,
        CancellationToken cancellationToken)
    {
        var source = TemplateSourceClass.Over(storage);
        await source.RemoveAsync(Template, Version);
        await source.PublishAsync(Template, Version, PackageFiles());

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

        var outcome = await take.TakeAsync(Template, Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(Version, outcome.Record!.Version);
    }

    // The sample template's own package with the corrections these cases need: the owner, one named
    // font family, and the version printed in the header.
    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);
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
                text = text.Replace("id: pilot-simple", "id: " + Template, StringComparison.Ordinal);
            }

            if (string.Equals(relative, "html/styles.css", StringComparison.Ordinal))
            {
                text = text.Replace("font-family: sans-serif;", "font-family: \"Liberation Sans\";", StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Sample, "sample.json")))!;
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
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
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

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    /// <summary>One answer of the listing.</summary>
    private sealed record Listing(string ReportJobId, IReadOnlyList<ListedArtifact> Artifacts);

    /// <summary>One artifact the listing carries.</summary>
    private sealed record ListedArtifact(
        string ArtifactId,
        string Format,
        string MimeType,
        long SizeBytes,
        string Sha256,
        DateTimeOffset ExpiresAt);

    /// <summary>One answer of the job status read.</summary>
    private sealed record StatusAnswer(string Status, IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of a job's answer.</summary>
    private sealed record OutputAnswer(string Format, string Status, string? ArtifactId);
}
