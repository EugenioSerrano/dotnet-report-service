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
/// The HTML output through the orchestration path, over the whole local composition
/// (US-008.BOLT-002, SPEC-261125-1224 §8.2; AC-4/5/6, ADR-048). These cases are authored in the
/// V-Bounce and <b>run on the <c>e2e</c> level</b> (Bolt <c>test_integration: required</c>); here
/// they must compile and pass static checks, they are not run to green now. The oracles come from
/// the approved intent (US-008 AC-4/5/6, ADR-048 R2/R3, ADR-047), never from the implementation
/// (G06).
/// <para>
/// Job-level observables only — the e2e suite has <b>no Temporal client</b> (no
/// <c>Temporalio</c> PackageReference; §9 forbids adding one, M1): the no-<c>ReportPdfOutput</c>-
/// child / no-<c>report-pdf-rendering</c>-activity guarantee is asserted HERMETICALLY in
/// <c>ReportJobWorkflowHtmlBranchTests</c> (§8.1). Here AC-4 proves only that a real HTML-only job
/// runs to a persisted HTML artifact and NO PDF artifact row.
/// </para>
/// <para>
/// The three fixture packages are built IN CODE, named by content (no <c>generic-raw</c> tree
/// asset): the HTML-capable sample (declares <c>outputs.html</c>, packaged relative CSS); the
/// non-HTML package (raw-XLSX-only, no <c>outputs.html</c>); and <c>html-fails-pdf-prints</c>
/// (printable <c>outputs.pdf</c> plus an <c>outputs.html</c> part referencing a non-inlinable
/// out-of-package asset, so the real assembler refuses the HTML at assembly per ADR-047
/// self-containment — a genuine definitive failure, no test seam). §15 (M2): if
/// <c>html-fails-pdf-prints</c> is rejected at publish/take before assembly, the V-Bounce stops
/// and surfaces a named seam under L2.
/// </para>
/// </summary>
public sealed class HtmlOutputEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan SettledDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "report-service-e2e-html", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public HtmlOutputEndToEndTests() => Directory.CreateDirectory(_directory);

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

    // AC-4: an HTML-only job over a pinned HTML-capable package runs to a succeeded HTML output,
    // exactly one text/html artifact, and no PDF artifact row (no PDF rendering took place).
    [Fact]
    public async Task HtmlOnlyJobProducesHtmlArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("html-capable", HtmlCapablePackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"html\"", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("SUCCEEDED", html.Status);
        Assert.NotNull(html.ArtifactId);
        Assert.Equal("text/html; charset=utf-8", html.MediaType);
        // No PDF artifact row exists: an HTML-only job started no PDF output.
        Assert.DoesNotContain(job.Outputs, output => output.Format == "PDF");
        Assert.DoesNotContain(job.Outputs, output => output.MediaType == "application/pdf");
    }

    // AC-5: an HTML job over a package that cannot produce HTML (raw-XLSX-only, no outputs.html)
    // ends the HTML output failed with UNSUPPORTED_CAPABILITY and no .html published.
    [Fact]
    public async Task HtmlUnsupportedPackageFailsHtmlOutputNoArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("non-html", NonHtmlPackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"html\"", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("FAILED", html.Status);
        Assert.Equal("UNSUPPORTED_CAPABILITY", html.ErrorCode);
        Assert.Null(html.ArtifactId);
    }

    // AC-6 (output isolation + rollup, ADR-048 R2): html+pdf where the HTML output fails
    // definitively — the PDF output stays succeeded and downloadable, the HTML output failed, and
    // the job rolls up to partially-succeeded on the real status record with no job-level error.
    [Fact]
    public async Task HtmlFailureKeepsPdfOutputStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("html-fails-pdf-prints", HtmlFailsPdfPrintsPackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"pdf\",\"html\"", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        // ADR-048 R2: some succeeded, some failed → the distinct partially-succeeded terminal
        // state, with no job-level error code.
        Assert.Equal("PARTIALLY_SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);

        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("SUCCEEDED", pdf.Status);
        Assert.NotNull(pdf.ArtifactId);

        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("FAILED", html.Status);
        Assert.Null(html.ArtifactId);
    }

    // AC-6 (HTML-only, ADR-048 R3): an HTML-only job whose sole output fails ends the job failed.
    [Fact]
    public async Task HtmlOnlyDefinitiveFailureFailsJob()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync("non-html-sole", NonHtmlPackage(), cancellationToken);

        var reportJobId = await SubmitAsync(world, outputs: "\"html\"", cancellationToken);
        var job = await WaitForSettledAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("FAILED", job.Status);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("FAILED", html.Status);
    }

    // ---- fixtures built in code, named by content (SPEC §8.2, M2) ----

    // The HTML-capable sample: the committed pilot-simple package, with its owner set to this
    // deployment's consumer. It declares outputs.html and a packaged relative stylesheet.
    private static Dictionary<string, byte[]> HtmlCapablePackage() => SamplePackage(_ => { });

    // A raw-XLSX-only package: the pilot sample with its HTML output parts removed from the
    // manifest, so the package declares no outputs.html and cannot produce HTML (AC-5).
    private static Dictionary<string, byte[]> NonHtmlPackage() => SamplePackage(files =>
    {
        // Remove the html output parts from the manifest declaration and drop the html assets.
        if (files.TryGetValue("template.yaml", out var manifest))
        {
            var text = Encoding.UTF8.GetString(manifest);
            // Strip the outputs.html block; leave only a raw (non-HTML) output declaration.
            text = RemoveYamlBlock(text, "outputs.html");
            files["template.yaml"] = Encoding.UTF8.GetBytes(text);
        }

        foreach (var key in files.Keys.Where(key => key.StartsWith("html/", StringComparison.Ordinal)).ToList())
        {
            files.Remove(key);
        }
    });

    // html-fails-pdf-prints: a printable outputs.pdf plus an outputs.html part that references a
    // non-inlinable out-of-package asset, so the real assembler refuses the HTML at assembly per
    // ADR-047 self-containment — a genuine definitive HTML failure beside a printable PDF (AC-6).
    private static Dictionary<string, byte[]> HtmlFailsPdfPrintsPackage() => SamplePackage(files =>
    {
        if (files.TryGetValue("html/document-start.html.sbn", out var documentStart))
        {
            var text = Encoding.UTF8.GetString(documentStart)
                .Replace(
                    "<link rel=\"stylesheet\" href=\"html/styles.css\">",
                    "<link rel=\"stylesheet\" href=\"html/styles.css\">\n  <link rel=\"stylesheet\" href=\"https://host.example/remote.css\">",
                    StringComparison.Ordinal);
            files["html/document-start.html.sbn"] = Encoding.UTF8.GetBytes(text);
        }
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

    // Removes a simple top-level-keyed YAML block (the key line and its more-indented children).
    private static string RemoveYamlBlock(string yaml, string key)
    {
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        var skipping = false;
        foreach (var line in lines)
        {
            if (!skipping && line.TrimStart().StartsWith(key, StringComparison.Ordinal))
            {
                skipping = true;
                continue;
            }

            if (skipping)
            {
                // Stay in the block while the line is blank or indented (a child of the key).
                if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                {
                    continue;
                }

                skipping = false;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
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

    private async Task<string> SubmitAsync(World world, string outputs, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(5);
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
