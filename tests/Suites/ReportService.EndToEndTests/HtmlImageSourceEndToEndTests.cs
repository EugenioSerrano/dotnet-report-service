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
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// A record's image through the whole local composition: a job asking for PDF and HTML from a
/// package whose row prints <c>&lt;img src="{{ item.image }}"&gt;</c>. An image data URI in the
/// record is printed by both outputs; any other value - here a remote reference - fails each
/// output that renders it with <c>IMAGE_SOURCE_INVALID</c>, and the job's status rolls up from
/// its outputs. These cases are authored in the V-Bounce and <b>run on the <c>e2e</c> level</b>
/// in the integration test cycle; here they must compile and pass static checks only. The
/// oracles come from the approved intent (the image source rule of the data contract, the
/// terminal status rollup), never from the implementation. The take used here runs no check,
/// as in the HTML output cases this harness is copied from.
/// </summary>
public sealed class HtmlImageSourceEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan SettledDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "report-service-e2e-image", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public HtmlImageSourceEndToEndTests() => Directory.CreateDirectory(_directory);

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

    private const string Png =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAX+XDSwAAAABJRU5ErkJggg==";

    // An image data URI from the record is printed by both outputs.
    [Fact]
    public async Task AnImageDataUriFromTheRecordIsPrintedByThePdfAndTheHtmlOutputs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("image-source", ImagePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"pdf\",\"html\"", Png, cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);
        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("SUCCEEDED", pdf.Status);
        Assert.NotNull(pdf.ArtifactId);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("SUCCEEDED", html.Status);
        Assert.NotNull(html.ArtifactId);
    }

    // An SVG data URI that carries a script is an image: both outputs print it, and loaded as an
    // image it runs nothing and fetches nothing.
    [Fact]
    public async Task AScriptedSvgDataUriIsPrintedAsAnInertImage()
    {
        const string ScriptedSvg =
            "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxzY3JpcHQ+YWxlcnQoMSk8L3NjcmlwdD48L3N2Zz4=";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("image-source-svg", ImagePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"pdf\",\"html\"", ScriptedSvg, cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.All(job.Outputs, output => Assert.Equal("SUCCEEDED", output.Status));
    }

    // An image data URI above 1 MiB (within the 2 MiB record bound) fails the HTML output with
    // OUTPUT_LIMIT_EXCEEDED; the job asks for HTML only, since the PDF output bounds images in its
    // own validation pass.
    [Fact]
    public async Task AnImageAboveOneMebibyteFailsTheHtmlOutputWithTheOutputLimit()
    {
        var large = "data:image/png;base64," + new string('A', 1024 * 1024);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("image-source-large", ImagePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"html\"", large, cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("FAILED", html.Status);
        Assert.Equal("OUTPUT_LIMIT_EXCEEDED", html.ErrorCode);
        Assert.Null(html.ArtifactId);
    }

    // A remote reference in the image source fails both outputs; with no output succeeded the
    // job ends failed.
    [Fact]
    public async Task ARemoteImageSourceFailsBothOutputsWithItsCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("image-source-remote", ImagePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"pdf\",\"html\"", "https://host.example/x.png", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("FAILED", pdf.Status);
        Assert.Equal("IMAGE_SOURCE_INVALID", pdf.ErrorCode);
        Assert.Null(pdf.ArtifactId);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("FAILED", html.Status);
        Assert.Equal("IMAGE_SOURCE_INVALID", html.ErrorCode);
        Assert.Null(html.ArtifactId);
    }

    // A relative reference is refused on the printed page, where no rule governs it: the PDF
    // output fails with the image-source code.
    [Fact]
    public async Task ARelativeImageSourceFailsThePdfOutputWithItsCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("image-source-relative", ImagePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"pdf\"", "images/missing.png", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("FAILED", pdf.Status);
        Assert.Equal("IMAGE_SOURCE_INVALID", pdf.ErrorCode);
    }

    // ---- fixtures built in code, named by content (SPEC §8.2, M2) ----

    // The pilot sample with an image column: its row prints the record's image as the source
    // of an <img>, and its item contract declares the image field.
    private static Dictionary<string, byte[]> ImagePackage() => SamplePackage(files =>
    {
        var row = Encoding.UTF8.GetString(files["html/row.html.sbn"]).Replace(
            "</tr>",
            "<td><img alt=\"code\" src=\"{{ item.image | html.escape }}\"></td></tr>",
            StringComparison.Ordinal);
        files["html/row.html.sbn"] = Encoding.UTF8.GetBytes(row);

        var schema = JsonNode.Parse(files["schema.json"])!;
        schema["$defs"]!["item"]!["properties"]!["image"] = new JsonObject { ["type"] = "string" };
        files["schema.json"] = Encoding.UTF8.GetBytes(schema.ToJsonString());

        var sample = JsonNode.Parse(files["sample.json"])!;
        foreach (var item in sample["items"]!.AsArray())
        {
            item!["image"] = Png;
        }

        files["sample.json"] = Encoding.UTF8.GetBytes(sample.ToJsonString());
    });

    private static Dictionary<string, byte[]> SamplePackage(Action<Dictionary<string, byte[]>> mutate)
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

        mutate(files);
        return files;
    }

    private sealed record World(DistributedApplication App, NpgsqlDataSource DataSource, HttpClient Api, string Template)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await DataSource.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    private async Task<World> StartAsync(string templateSuffix, Dictionary<string, byte[]> package, CancellationToken cancellationToken)
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
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var template = Sample + "-" + templateSuffix;
        await TakeAsync(dataSource, template, package, cancellationToken);
        return new World(app, dataSource, app.CreateHttpClient("api"), template);
    }

    // Takes the fixture package into the composition's shared storage and metadata, the way a
    // second job over an already-taken version sees it — the render worker container reads no source.
    private async Task TakeAsync(
        NpgsqlDataSource dataSource,
        string template,
        Dictionary<string, byte[]> package,
        CancellationToken cancellationToken)
    {
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var versions = new PostgresTemplateVersionStore(dataSource);

        // The package is published under the fixture template id/owner: rewrite the manifest id.
        var files = package.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
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

    private async Task<string> SubmitAsync(World world, string outputs, string image, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(5, image);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new PostgresUploadStore(world.DataSource);
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

    private static byte[] Snapshot(int records, string image)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample, "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            item["image"] = image;
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    // Polls the real Postgres for the job and every output + artifact row (job-level observables
    // only — no Temporal client). It settles when the job reaches a terminal status.
    private static async Task<SettledJob> WaitForSettledAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        SettledJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to settle",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT j.status, j.error_code,
                           o.format, o.status, o.error_code, a.artifact_id, a.media_type
                      FROM report_service.report_jobs j
                      LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
                      LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                     WHERE j.report_job_id = @id
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                await using var reader = await command.ExecuteReaderAsync(token);
                string status = string.Empty;
                string? jobError = null;
                var outputs = new List<SettledOutput>();
                while (await reader.ReadAsync(token))
                {
                    status = reader.GetString(0);
                    jobError = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!reader.IsDBNull(2))
                    {
                        outputs.Add(new SettledOutput(
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6)));
                    }
                }

                job = new SettledJob(status, jobError, outputs);
                return status is "SUCCEEDED" or "FAILED" or "CANCELLED" or "PARTIALLY_SUCCEEDED";
            },
            SettledDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return job!;
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record SettledJob(string Status, string? ErrorCode, IReadOnlyList<SettledOutput> Outputs);

    private sealed record SettledOutput(string Format, string Status, string? ErrorCode, string? ArtifactId, string? MediaType);
}
