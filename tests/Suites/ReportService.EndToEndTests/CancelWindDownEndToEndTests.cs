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
using ReportService.Api.Artifacts;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// US-010 AC-2…AC-5 over the whole local composition: the cooperative wind-down, the terminal
/// <c>CANCELLED</c> and the per-output cancellation rollup, driven through the real API, the
/// orchestration worker, Temporal, PostgreSQL, the render-worker container (Chromium) and the
/// filesystem object storage.
/// <para>
/// The oracles are the approved US-010 criteria and the accepted ADR-012 §3.10 / ADR-048 §3.1
/// (§3.4) rules, never the implementation (G06). A1 and A2 cancel real submitted jobs and assert
/// the consumer-visible terminal facts; A3 races a cancellation against a two-output job whose
/// HTML output already published, and asserts the prior success is kept while the job still rolls
/// up <c>CANCELLED</c> by R0 — never <c>PARTIALLY_SUCCEEDED</c>; A4 drives the
/// cancelled-before-start path deterministically from a seeded state, so the rollup and the
/// intent-consumed predicate never depend on winning a render race.
/// </para>
/// <para>
/// These cases run in the Bolt's integration test cycle, not in the V-Bounce (§8.2): they need the
/// composition's container runtime, the render-worker image and the local development registry.
/// </para>
/// </summary>
public sealed class CancelWindDownEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private const string CancelJobKind = "cancel-job";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan SettledDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RunningDeadline = TimeSpan.FromMinutes(3);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-cancel-wind-down",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public CancelWindDownEndToEndTests() => Directory.CreateDirectory(_directory);

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

    /// <summary>
    /// A1 — a job cancelled immediately after it is submitted ends <c>CANCELLED</c>: every output
    /// is <c>CANCELLED</c>, the job's <c>finished_at</c> is set, and no artifact was published for
    /// it. Holds at any stage the cancellation reached: with the cancel recorded before the
    /// execution produced anything, the run sees <c>CANCELLING</c> on its first step and closes the
    /// job instead of producing (ADR-012 §3.10(2), US-010 AC-3/AC-4).
    /// </summary>
    [Fact]
    public async Task A1_AJobCancelledImmediatelyEndsCancelledWithNoArtifact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "immediate", cancellationToken);

        var reportJobId = await SubmitAsync(world, template, records: 25, outputs: "\"pdf\"", cancellationToken);
        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("CANCELLED", output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));
    }

    /// <summary>
    /// A2 — a job observed running is cancelled: it ends <c>CANCELLED</c> with its output
    /// <c>CANCELLED</c>, no artifact row exists for it and no block part was committed after the
    /// cancel request's instant (the commit guard's observable, AC-5), and the render worker still
    /// serves its one render slot afterwards — a following small job succeeds, so the cancelled
    /// render left no orphan behind (AC-2).
    /// </summary>
    [Fact]
    public async Task A2_ACancelledRunningJobPublishesNothingAfterTheCancelAndTheWorkerStaysHealthy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "running", cancellationToken);

        var reportJobId = await SubmitAsync(world, template, records: 60, outputs: "\"pdf\"", cancellationToken);
        await WaitUntilStatusAsync(world.DataSource, reportJobId, "RUNNING", RunningDeadline, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var requestedAt = await CancelIntentInstantAsync(world.DataSource, reportJobId, cancellationToken);
        Assert.NotNull(requestedAt);

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("CANCELLED", output.Status);
        Assert.Null(output.ArtifactId);
        Assert.Equal(0, await ArtifactCountAsync(world.DataSource, reportJobId, cancellationToken));
        Assert.Equal(
            0,
            await PartCommitsAfterAsync(world.DataSource, reportJobId, requestedAt!.Value, cancellationToken));

        // The render worker still renders: the cancellation released the single render slot and
        // left no orphan render behind (AC-2's worker-health check).
        var followUpId = await SubmitAsync(world, template, records: 10, outputs: "\"pdf\"", cancellationToken);
        var followUp = await WaitForTerminalAsync(world.DataSource, followUpId, cancellationToken);
        Assert.Equal("SUCCEEDED", followUp.Status);
        Assert.NotNull(Assert.Single(followUp.Outputs).ArtifactId);
    }

    /// <summary>
    /// A3 — a cancellation after one output already published: the HTML output stays
    /// <c>SUCCEEDED</c> and its artifact downloadable, the PDF output ends <c>CANCELLED</c>, and the
    /// job rolls up <c>CANCELLED</c> by R0 — never <c>PARTIALLY_SUCCEEDED</c> (ADR-048 §3.1 R0
    /// over R2, §3.4). The cancel must be accepted: a <c>200</c> means the fixture finished before
    /// the cancellation reached it, which fails the case with that diagnostic and is never retried
    /// to green (SPEC §8.2 A3).
    /// </summary>
    [Fact]
    public async Task A3_APriorSuccessIsKeptAndTheJobStillRollsUpCancelled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "prior-success", cancellationToken);

        // A document large enough that the PDF render is still in flight while the HTML assembly
        // has already published its artifact.
        var reportJobId = await SubmitAsync(world, template, records: 300, outputs: "\"pdf\",\"html\"", cancellationToken);
        var succeeded = await WaitForHttpSucceededWhileRunningAsync(world, reportJobId, cancellationToken);

        using var cancel = await CancelAsync(world, reportJobId, cancellationToken);
        Assert.True(
            cancel.StatusCode == HttpStatusCode.Accepted,
            $"the cancellation had to be accepted, and the fixture finished before it reached the job ({(int)cancel.StatusCode}); the case fails with this diagnostic and is never retried to green.");

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotEqual("PARTIALLY_SUCCEEDED", job.Status);
        Assert.Null(job.ErrorCode);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("SUCCEEDED", html.Status);
        Assert.NotNull(html.ArtifactId);
        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("CANCELLED", pdf.Status);
        Assert.Null(pdf.ArtifactId);

        // AC-5: the artifact published before the cancellation stays downloadable.
        using var download = await DownloadAsync(world, reportJobId, html.ArtifactId!, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(succeeded.ArtifactSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    /// <summary>
    /// A4 — cancelled before its workflow started, deterministically: a two-output job is seeded
    /// through the composition's own stores, one output is published as a real artifact, and the
    /// cancellation is requested before any execution produced anything. The real dispatcher
    /// delivers the intent and the real workflow closes the job: <c>CANCELLED</c> by R0, the
    /// succeeded output and its artifact kept, the other output <c>CANCELLED</c>, and exactly one
    /// <c>cancel-job</c> row <c>DISPATCHED</c> with its instant and no row left <c>PENDING</c>
    /// (the intent-consumed predicate, SPEC §14 D8).
    /// </summary>
    [Fact]
    public async Task A4_AJobCancelledBeforeItStartedEndsCancelledKeepingItsPriorSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var world = await StartAsync(cancellationToken);
        var template = await TakeTemplateAsync(world.DataSource, "before-start", cancellationToken);

        var (reportJobId, pdfOutputId, htmlOutputId, artifactId, artifactSha256) =
            await SeedCancelledBeforeStartAsync(world, template, cancellationToken);

        var job = await WaitForTerminalAsync(world.DataSource, reportJobId, cancellationToken);

        Assert.Equal("CANCELLED", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorCode);
        var pdf = Assert.Single(job.Outputs, output => output.Format == "PDF");
        Assert.Equal("SUCCEEDED", pdf.Status);
        Assert.Equal(artifactId, pdf.ArtifactId);
        var html = Assert.Single(job.Outputs, output => output.Format == "HTML");
        Assert.Equal("CANCELLED", html.Status);
        Assert.Null(html.ArtifactId);

        // AC-5: the artifact published before the cancellation stays downloadable.
        using var download = await DownloadAsync(world, reportJobId, artifactId, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        Assert.Equal(artifactSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        // D8's exact predicate: one cancel-job row, DISPATCHED with its instant, none pending.
        var intents = await CancelIntentsAsync(world.DataSource, reportJobId, cancellationToken);
        var dispatched = Assert.Single(intents);
        Assert.Equal("DISPATCHED", dispatched.Status);
        Assert.NotNull(dispatched.DispatchedAt);
        Assert.DoesNotContain(intents, intent => intent.Status == "PENDING");

        // The outputs the seed created are the ones the closure wrote, so the case cannot pass on
        // a job that never had them.
        Assert.Equal(pdfOutputId, pdf.OutputId);
        Assert.Equal(htmlOutputId, html.OutputId);
    }

    // ---- composition and fixtures ----

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
        var dataSource = NpgsqlDataSource.Create(connectionString);
        return new World(app, dataSource, app.CreateHttpClient("api"));
    }

    // Takes the fixture package into the composition's shared storage and metadata, the way a
    // second job over an already-taken version sees it — the render worker container reads no
    // template source.
    private static async Task<string> TakeTemplateAsync(
        NpgsqlDataSource dataSource,
        string suffix,
        CancellationToken cancellationToken)
    {
        var template = Sample + "-cancel-" + suffix;
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var versions = new PostgresTemplateVersionStore(dataSource);

        var files = SamplePackage();
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
        return template;
    }

    private static Dictionary<string, byte[]> SamplePackage()
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

    private static async Task<string> SubmitAsync(
        World world,
        string template,
        int records,
        string outputs,
        CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(records);
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
                "{\"template\":{\"id\":\"" + template + "\",\"version\":\"" + Version + "\"},"
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

    // A two-output job seeded through the composition's own stores: accepted (which records its
    // start-job intent), its PDF output published as a real artifact written under the storage
    // root, its HTML output left pending, and the cancellation requested — all before any
    // execution produced anything. This is the state a cancel delivered before the start, or
    // before the start was dispatched, leaves the service in.
    private static async Task<(string ReportJobId, string PdfOutputId, string HtmlOutputId, string ArtifactId, string ArtifactSha256)>
        SeedCancelledBeforeStartAsync(World world, string template, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var uploads = new PostgresUploadStore(world.DataSource);
        var jobs = new PostgresJobStore(world.DataSource);

        var snapshot = Snapshot(25);
        var uploadId = await ReadyUploadAsync(uploads, storage, snapshot, cancellationToken);

        var pdfOutputId = JobIdentifier.CreateOutput();
        var htmlOutputId = JobIdentifier.CreateOutput();
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(pdfOutputId, OutputFormat.Pdf), new RequestedOutput(htmlOutputId, OutputFormat.Html)],
            Consumer,
            uploadId,
            template,
            TemplateReferenceForm.Exact,
            Version,
            AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await jobs.AcceptAsync(acceptance, now, cancellationToken));

        // One output already succeeded before the cancellation, with its real artifact object.
        Assert.Equal(TransitionOutcome.Applied, await jobs.TransitionOutputAsync(pdfOutputId, now, cancellationToken));
        var artifactBytes = Encoding.UTF8.GetBytes("%PDF-1.4 the artifact published before the cancellation");
        var objectKey = ObjectKey.FromSegments("outputs", acceptance.ReportJobId, pdfOutputId, "a");
        using (var content = new MemoryStream(artifactBytes, writable: false))
        {
            var written = await storage.WriteAsync(objectKey, content, cancellationToken);
            Assert.True(written.Succeeded);
        }

        var artifactSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifactBytes));
        var artifactId = ArtifactIdentifier.Create();
        var published = await jobs.PublishAsync(
            new ArtifactRecord(
                artifactId,
                acceptance.ReportJobId,
                pdfOutputId,
                OutputFormat.Pdf,
                "application/pdf",
                objectKey.ToString(),
                artifactSha256,
                artifactBytes.LongLength,
                1,
                now,
                now.AddDays(7)),
            cancellationToken);
        Assert.Equal(PublishOutcome.Published, published.Outcome);

        var cancelled = await jobs.RequestCancellationAsync(
            acceptance.ReportJobId,
            Consumer,
            DateTimeOffset.UtcNow,
            cancellationToken);
        Assert.IsType<CancellationRequestResult.Transitioned>(cancelled);

        return (acceptance.ReportJobId, pdfOutputId, htmlOutputId, artifactId, artifactSha256);
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

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    // ---- driving and reading the real composition ----

    private static Task<HttpResponseMessage> CancelAsync(World world, string reportJobId, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}/cancel", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return world.Api.SendAsync(message, cancellationToken);
    }

    private static Task<HttpResponseMessage> DownloadAsync(
        World world,
        string reportJobId,
        string artifactId,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        return world.Api.SendAsync(message, cancellationToken);
    }

    private static async Task WaitUntilStatusAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        string expected,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        await Wait.UntilAsync(
            $"job {reportJobId} to read {expected}",
            async token => string.Equals(await JobStatusAsync(dataSource, reportJobId, token), expected, StringComparison.Ordinal),
            deadline,
            TimeSpan.FromMilliseconds(200),
            cancellationToken);
    }

    // Polls the real PostgreSQL until the job reaches a terminal status and reads the job with
    // every output and the identifier of its published artifact.
    private static async Task<SettledJob> WaitForTerminalAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        SettledJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to settle",
            async token =>
            {
                job = await ReadJobAsync(dataSource, reportJobId, token);
                return job.Status is "SUCCEEDED" or "FAILED" or "CANCELLED" or "PARTIALLY_SUCCEEDED";
            },
            SettledDeadline,
            TimeSpan.FromMilliseconds(300),
            cancellationToken);
        return job!;
    }

    // Waits until the job has one SUCCEEDED output and is still running: the state A3 cancels in.
    private static async Task<SettledOutput> WaitForHttpSucceededWhileRunningAsync(
        World world,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        SettledOutput? succeeded = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to publish its HTML output while still running",
            async token =>
            {
                var job = await ReadJobAsync(world.DataSource, reportJobId, token);
                succeeded = job.Outputs.FirstOrDefault(output => output.Format == "HTML" && output.Status == "SUCCEEDED");
                return succeeded is not null && string.Equals(job.Status, "RUNNING", StringComparison.Ordinal);
            },
            RunningDeadline,
            TimeSpan.FromMilliseconds(200),
            cancellationToken);

        // The SHA-256 the record carries is what the published artifact must download as.
        return succeeded!;
    }

    private static async Task<string> JobStatusAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM report_service.report_jobs WHERE report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<SettledJob> ReadJobAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT j.status, j.error_code, j.finished_at,
                   o.output_id, o.format, o.status, o.error_code, o.finished_at,
                   a.artifact_id, a.sha256
              FROM report_service.report_jobs j
              LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
              LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
             WHERE j.report_job_id = @id
             ORDER BY o.format
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var status = string.Empty;
        string? errorCode = null;
        DateTime? finishedAt = null;
        var outputs = new List<SettledOutput>();
        while (await reader.ReadAsync(cancellationToken))
        {
            status = reader.GetString(0);
            errorCode = reader.IsDBNull(1) ? null : reader.GetString(1);
            finishedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            if (!reader.IsDBNull(3))
            {
                outputs.Add(new SettledOutput(
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        return new SettledJob(status, errorCode, finishedAt, outputs);
    }

    private static async Task<int> ArtifactCountAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM report_service.artifacts WHERE report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> PartCommitsAfterAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        DateTimeOffset instant,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*)
              FROM report_service.pdf_part_commits pc
              JOIN report_service.report_outputs o ON o.output_id = pc.output_id
             WHERE o.report_job_id = @id AND pc.committed_at > @instant
            """;
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        command.Parameters.Add(new NpgsqlParameter("instant", instant.UtcDateTime));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> CancelIntentInstantAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT min(created_at) FROM report_service.intents WHERE kind = @kind AND subject_id = @id";
        command.Parameters.Add(new NpgsqlParameter("kind", CancelJobKind));
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private static async Task<IReadOnlyList<CancelIntent>> CancelIntentsAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, dispatched_at
              FROM report_service.intents
             WHERE kind = @kind AND subject_id = @id
             ORDER BY intent_id
            """;
        command.Parameters.Add(new NpgsqlParameter("kind", CancelJobKind));
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var intents = new List<CancelIntent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            intents.Add(new CancelIntent(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc), TimeSpan.Zero)));
        }

        return intents;
    }

    private sealed record World(DistributedApplication App, NpgsqlDataSource DataSource, HttpClient Api) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Api.Dispose();
            await DataSource.DisposeAsync();
            await App.DisposeAsync();
        }
    }

    private sealed record SettledJob(
        string Status,
        string? ErrorCode,
        DateTime? FinishedAt,
        IReadOnlyList<SettledOutput> Outputs);

    private sealed record SettledOutput(
        string OutputId,
        string Format,
        string Status,
        string? ErrorCode,
        DateTime? FinishedAt,
        string? ArtifactId,
        string? ArtifactSha256);

    private sealed record CancelIntent(string Status, DateTimeOffset? DispatchedAt);
}
