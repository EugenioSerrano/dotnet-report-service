using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Keys;
using ReportService.TemplatePackages.Packages;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// Following a report job on the whole local composition: the <c>statusUrl</c> the creation
/// answered with is polled through the real HTTP pipeline, and the answers tell a consumer
/// what happened to its report — a snapshot the data step refuses ends failed with the code
/// the profile names on the job and on its PDF output, a document over the block maximum ends
/// failed with the quota code on both, and a job of another consumer, an absent identifier and
/// a request with no identity are answered exactly as the service answers them.
/// <para>
/// The published and empty-snapshot readings belong to the integration suite's published
/// class: they need the steps that render and publish, which this build does not carry.
/// Nothing on this commit takes a template version, so the class installs the pilot's own
/// before its first job, exactly as the integration classes do: the schema is packaged, written
/// under the content-addressed key of its bytes into the composition's shared storage root and
/// recorded in the composition's own database.
/// </para>
/// </summary>
public sealed class JobStatusEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [
            { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true },
            { "consumerId": "consumer-b", "displayName": "Consumer B", "active": true },
            { "consumerId": "consumer-c", "displayName": "Consumer C", "active": false } ] }
        """;

    /// <summary>The template every job of this class is accepted for.</summary>
    private const string TemplateId = "pilot-simple";

    /// <summary>The version every job of this class names.</summary>
    private const string Version = "1.0.0";

    /// <summary>The path of the schema inside the taken package.</summary>
    private const string SchemaPath = "schema.json";

    /// <summary>The pilot's own sample document of that template.</summary>
    private const string SamplePath = "sample.json";

    private const string ConsumerA = "consumer-a";

    private const string ConsumerB = "consumer-b";

    /// <summary>The interval the status URL is polled at.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    private string _sampleHeader = "{}";

    private IReadOnlyList<string> _sampleRecords = [];

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public JobStatusEndToEndTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ARefusedSnapshotEndsFailedWithItsCodeOnTheJobAndOnItsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await SeedTakenVersionAsync(connectionString, cancellationToken);

        var uploadId = await ReadyUploadAsync(api, ConsumerA, RefusedDocument(), cancellationToken);
        var reportJobId = await AcceptJobAsync(api, ConsumerA, uploadId, cancellationToken);

        var answer = await WaitForTerminalAsync(api, reportJobId, cancellationToken);
        Assert.Equal("failed", answer.Status);
        Assert.Equal("DATA_INVALID", answer.ErrorCode);
        Assert.NotNull(answer.FinishedAt);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("failed", output.Status);
        Assert.Equal("DATA_INVALID", output.ErrorCode);
        Assert.Null(output.ArtifactId);
    }

    [Fact]
    public async Task ADocumentOverTheBlockMaximumEndsFailedWithTheQuotaCodeOnBoth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await SeedTakenVersionAsync(connectionString, cancellationToken);

        var uploadId = await ReadyUploadAsync(api, ConsumerA, Document(Records(2_001)), cancellationToken);
        var reportJobId = await AcceptJobAsync(api, ConsumerA, uploadId, cancellationToken);

        var answer = await WaitForTerminalAsync(api, reportJobId, cancellationToken);
        Assert.Equal("failed", answer.Status);
        Assert.Equal("QUOTA_EXCEEDED", answer.ErrorCode);
        Assert.Null(answer.RecordsTotal);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("failed", output.Status);
        Assert.Equal("QUOTA_EXCEEDED", output.ErrorCode);
        Assert.Null(output.ArtifactId);
    }

    [Fact]
    public async Task AnotherConsumersJobAndAnAbsentIdentifierAnswerTheSameRefusalAndNoIdentityIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await SeedTakenVersionAsync(connectionString, cancellationToken);

        // A job of consumer-b, read by consumer-a: it is indistinguishable from a job that
        // does not exist, down to the answer's members apart from the trace identifier.
        var uploadId = await ReadyUploadAsync(api, ConsumerB, Document(Records(1)), cancellationToken);
        var jobOfB = await AcceptJobAsync(api, ConsumerB, uploadId, cancellationToken);

        var foreign = await ReadProblemAsync(api, jobOfB, ConsumerA, cancellationToken);
        var absent = await ReadProblemAsync(api, "job-" + new string('f', 32), ConsumerA, cancellationToken);
        var malformed = await ReadProblemAsync(api, "job-XYZ", ConsumerA, cancellationToken);

        Assert.Equal(foreign with { TraceId = null }, absent with { TraceId = null });
        Assert.Equal(foreign with { TraceId = null }, malformed with { TraceId = null });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("INPUT_NOT_FOUND", foreign.Code);
        Assert.Equal(ReportJobStatusEndpoint.NotFoundTitle, foreign.Title);
        Assert.Equal(ReportJobStatusEndpoint.NotFoundDetail, foreign.Detail);

        // A request that declares no identity is the registry gate's own refusal.
        using var anonymous = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobOfB}", UriKind.Relative)),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        using var problem = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal(ErrorCodes.ConsumerUnknown, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", TemplateId, SamplePath),
            cancellationToken))!;
        _sampleHeader = sample["header"]!.ToJsonString();
        _sampleRecords = [.. sample["items"]!.AsArray().Select(record => record!.ToJsonString())];

        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);
        var appHost = await TestComposition.CreateAsync(cancellationToken, $"--Consumers:File={registry}");
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        return app;
    }

    private static async Task<string> ConnectionStringAsync(DistributedApplication app, CancellationToken cancellationToken) =>
        await app.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");

    // The taken version this class's jobs are pinned to, in the composition's own database and
    // shared storage root: the pilot's schema, packaged through the archive a take uses, written
    // under the content-addressed key of its bytes and recorded with the written package's size,
    // hash and file count.
    private static async Task SeedTakenVersionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllBytesAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", TemplateId, SchemaPath),
            cancellationToken);
        var files = new List<PackageFileSource>
        {
            new(SchemaPath, _ => Task.FromResult<Stream>(new MemoryStream(schema, writable: false))),
        };

        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, cancellationToken);
        var bytes = sink.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(ConsumerA, TemplateId, Version, packageSha256);

        // The composition's one storage root, shared by every process of the run.
        var root = Path.Combine(RepositoryPaths.Root, ".storage");
        Directory.CreateDirectory(root);
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        using (var content = new MemoryStream(bytes, writable: false))
        {
            var written = await objects.WriteAsync(key, content, cancellationToken);
            Assert.True(written.Succeeded, "the taken package had to be written");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var versions = new PostgresTemplateVersionStore(dataSource);
        var inserted = await versions.TryInsertAsync(
            TemplateVersionRecords.Sample(
                TemplateId,
                Version,
                ConsumerA,
                shared: true,
                schemaRef: SchemaPath,
                packageSha256: packageSha256) with
            {
                PackageSize = bytes.Length,
                FileCount = files.Count,
                ObjectKey = key,
            },
            cancellationToken);
        Assert.True(inserted.WasInserted, "the taken version had to be recorded");
    }

    private async Task<string> ReadyUploadAsync(HttpClient api, string consumer, byte[] document, CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        var uploadId = createdDocument.RootElement.GetProperty("uploadId").GetString()!;

        using (var content = new ByteArrayContent(document))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var sent = await api.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
                {
                    Content = content,
                    Headers = { { ConsumerIdentity.HeaderName, consumer } },
                },
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, consumer } },
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        await Wait.UntilAsync(
            $"the upload {uploadId} to end ready",
            async token =>
            {
                using var read = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                read.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
                using var response = await api.SendAsync(read, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var projection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return projection.RootElement.GetProperty("status").GetString() == "ready";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return uploadId;
    }

    private static async Task<string> AcceptJobAsync(HttpClient api, string consumer, string uploadId, CancellationToken cancellationToken)
    {
        using var accepted = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent(
                    $$"""{"template":{"id":"{{TemplateId}}","version":"{{Version}}"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                    Encoding.UTF8,
                    "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    // The status URL the creation answered with, polled on the case's own interval until the
    // job ends: the demo transcript is these answers, in order.
    private async Task<Answer> WaitForTerminalAsync(HttpClient api, string reportJobId, CancellationToken cancellationToken)
    {
        Answer? last = null;
        await Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token =>
            {
                last = await ReadAsync(api, reportJobId, ConsumerA, token);
                return last.Status is "failed" or "succeeded" or "cancelled";
            },
            TerminalDeadline,
            PollInterval,
            cancellationToken);
        return last!;
    }

    private static async Task<Answer> ReadAsync(HttpClient api, string reportJobId, string consumer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "every status answer has to be uncacheable");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var progress = root.GetProperty("progress");
        return new Answer(
            root.GetProperty("status").GetString()!,
            root.GetProperty("stage") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("stage").GetString(),
            progress.GetProperty("recordsRead").GetInt64(),
            progress.GetProperty("recordsTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("recordsTotal").GetInt64(),
            progress.GetProperty("blocksCompleted").GetInt32(),
            progress.GetProperty("blocksTotal") is { ValueKind: JsonValueKind.Null } ? null : progress.GetProperty("blocksTotal").GetInt32(),
            progress.GetProperty("pagesGenerated").GetInt64(),
            root.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorCode").GetString(),
            root.GetProperty("finishedAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("finishedAt").GetString(),
            [
                .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                    element.GetProperty("format").GetString()!,
                    element.GetProperty("status").GetString()!,
                    element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString(),
                    element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString())),
            ]);
    }

    // One refusal, as the service answered it: the shape a consumer branches on.
    private static async Task<Problem> ReadProblemAsync(
        HttpClient api,
        string reportJobId,
        string consumer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        return new Problem(
            response.StatusCode,
            root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.TryGetProperty("title", out var title) ? title.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            root.TryGetProperty("traceId", out var trace) ? trace.GetString() : null);
    }

    // The records of a document of the case, built from the pilot's own records: the schema of
    // the pinned version admits every one of them, and the sequence is the record's position.
    private IReadOnlyList<string> Records(int count)
    {
        var records = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var item = JsonNode.Parse(_sampleRecords[index % _sampleRecords.Count])!.AsObject();
            item["sequence"] = index + 1;
            records.Add(item.ToJsonString());
        }

        return records;
    }

    private byte[] Document(IReadOnlyList<string> records) =>
        Encoding.UTF8.GetBytes(
            """{"contractVersion":"1.0","header":"""
            + _sampleHeader
            + ""","items":["""
            + string.Join(',', records)
            + "]}");

    // A document whose records come before its header: the profile refuses it at the items
    // member, before any record is captured, and the job ends with the profile's own code.
    private byte[] RefusedDocument() =>
        Encoding.UTF8.GetBytes(
            """{"contractVersion":"1.0","items":[{"sequence":1,"description":"","quantity":1,"unitPrice":1.00,"amount":1.00}],"header":"""
            + _sampleHeader
            + "}");

    /// <summary>One answer of the status read, as the case sampled it.</summary>
    private sealed record Answer(
        string Status,
        string? Stage,
        long RecordsRead,
        long? RecordsTotal,
        int BlocksCompleted,
        int? BlocksTotal,
        long PagesGenerated,
        string? ErrorCode,
        string? FinishedAt,
        IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of an answer.</summary>
    private sealed record OutputAnswer(string Format, string Status, string? ArtifactId, string? ErrorCode);

    /// <summary>One refusal, as the composition answered it.</summary>
    private sealed record Problem(HttpStatusCode StatusCode, string? Code, string? Title, string? Detail, string? TraceId);
}
