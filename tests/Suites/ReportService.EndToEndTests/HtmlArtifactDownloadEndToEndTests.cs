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
/// US-008 AC-8 over the whole local composition: after an HTML-only job succeeds, the produced
/// <c>.html</c> appears in the job's artifact listing and is downloaded the same way the PDF is —
/// listed with format <c>html</c> and media type <c>text/html; charset=utf-8</c>, downloaded with
/// <c>Content-Type: text/html; charset=utf-8</c>, filename <c>{jobId}.html</c> and the stored
/// bytes (US-008.BOLT-003, SPEC-261126-0051 §8.2). These cases are authored in the V-Bounce and
/// <b>run on the <c>e2e</c> level</b> (Bolt <c>test_integration: required</c>); here they must
/// compile and pass static checks, they are not run to green now. The oracles come from the
/// approved intent (US-008 AC-8, ADR-047 HTML-output contract), never from the implementation (G06).
/// <para>
/// This Bolt's e2e evidence asserts <b>output-level and download observables only</b>; it does not
/// assert job-level status (that is ADR-048 / US-008.BOLT-002, verified at
/// <c>HtmlOutputEndToEndTests</c>). The artifact is awaited through the public listing endpoint,
/// so the oracle is the served contract, not the database rollup.
/// </para>
/// </summary>
public sealed class HtmlArtifactDownloadEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private const string HtmlMediaType = "text/html; charset=utf-8";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan PublishedDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-html-download",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public HtmlArtifactDownloadEndToEndTests() => Directory.CreateDirectory(_directory);

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

    // AC-8: an HTML-only job's produced .html is listed (format html, media type
    // text/html; charset=utf-8) and downloaded with those headers, the {jobId}.html filename and the
    // stored bytes — verified against the hash and size the listing carries.
    [Fact]
    public async Task HtmlOnlyJobArtifactIsListedAndDownloadedWithTheHtmlContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"html\"", cancellationToken);

        // Awaited through the public listing endpoint — output-level observables only, no job status.
        var listed = await WaitForHtmlArtifactAsync(world.Api, reportJobId, cancellationToken);
        Assert.Equal("html", listed.Format);
        Assert.Equal(HtmlMediaType, listed.MimeType);

        using var response = await DownloadAsync(world.Api, reportJobId, listed.ArtifactId, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HtmlMediaType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal($"{reportJobId}.html", disposition.FileName?.Trim('"'));

        Assert.Equal(listed.SizeBytes, response.Content.Headers.ContentLength);
        var installed = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(listed.SizeBytes, installed.LongLength);
        Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(installed)));
    }

    // Polls the public listing until the HTML artifact is published and served, then returns it.
    private static async Task<ListedArtifact> WaitForHtmlArtifactAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        ListedArtifact? html = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to list its HTML artifact",
            async token =>
            {
                var listing = await ListAsync(api, reportJobId, token);
                html = listing.FirstOrDefault(artifact => artifact.Format == "html");
                return html is not null;
            },
            PublishedDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return html!;
    }

    private static async Task<IReadOnlyList<ListedArtifact>> ListAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
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
                artifact.GetProperty("sha256").GetString()!));
        }

        return artifacts;
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

    private sealed record World(DistributedApplication App, HttpClient Api, string Template) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await App.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

        var builder = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var health = app.CreateHttpClient("api"))
        {
            await Wait.UntilAsync(
                "the API answering /health",
                async token =>
                {
                    using var response = await health.GetAsync(new Uri("/health", UriKind.Relative), token);
                    return response.StatusCode == HttpStatusCode.OK;
                },
                HealthyDeadline,
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
        }

        var connectionString = await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var template = Sample + "-html-download";
        await TakeAsync(dataSource, template, cancellationToken);
        return new World(app, app.CreateHttpClient("api"), template);
    }

    // Takes the HTML-capable pilot package into the composition's shared storage and metadata, so a
    // job over the taken version produces an HTML artifact (the render worker reads no source).
    private async Task TakeAsync(NpgsqlDataSource dataSource, string template, CancellationToken cancellationToken)
    {
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var versions = new PostgresTemplateVersionStore(dataSource);

        var files = HtmlCapablePackage();
        if (files.TryGetValue("template.yaml", out var manifest))
        {
            var text = Encoding.UTF8.GetString(manifest).Replace("id: " + Sample, "id: " + template, StringComparison.Ordinal);
            files["template.yaml"] = Encoding.UTF8.GetBytes(text);
        }

        var source = TemplateSourceClass.Over(storage);
        await source.RemoveAsync(template, Version);
        await source.PublishAsync(template, Version, files);

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
        var outcome = await take.TakeAsync(template, Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
    }

    // The HTML-capable sample: the committed pilot-simple package, with its owner set to this
    // deployment's consumer. It declares outputs.html and a packaged relative stylesheet.
    private static Dictionary<string, byte[]> HtmlCapablePackage()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    private async Task<string> SubmitAsync(World world, string outputs, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(5);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var connectionString = await world.App.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var uploads = new PostgresUploadStore(dataSource);
        var uploadId = await ReadyUploadAsync(uploads, storage, snapshot, cancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + world.Template + "\",\"version\":\"" + Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},\"outputs\":[" + outputs + "]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await world.Api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

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

    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample, "sample.json")))!;
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

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    /// <summary>One artifact the listing carries.</summary>
    private sealed record ListedArtifact(
        string ArtifactId,
        string Format,
        string MimeType,
        long SizeBytes,
        string Sha256);
}
