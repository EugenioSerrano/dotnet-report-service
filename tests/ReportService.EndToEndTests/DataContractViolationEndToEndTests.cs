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
/// A snapshot that breaks the data contract, followed the way a consumer follows its report:
/// uploaded and confirmed against the running composition (the confirmation does not check the
/// contract), a job accepted for it, and the status read polled until it ends. Every kind of
/// violation the product refuses — a header after the items, a truncated document, content after
/// the closing, a record the pinned template's schema refuses and a repeated member — ends the
/// job failed with the data-contract code on the job and on its PDF output, with no artifact to
/// list and no planted value anywhere in the answers. The same schema refusal in the last record
/// of a two-thousand-record snapshot ends the job the same way.
/// </summary>
public sealed class DataContractViolationEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }
        """;

    /// <summary>The template every job of this class names.</summary>
    private const string Template = "pilot-simple";

    /// <summary>The version every job of this class names.</summary>
    private const string Version = "1.0.0";

    /// <summary>The path of the schema inside the taken package.</summary>
    private const string SchemaPath = "schema.json";

    /// <summary>The pilot's own sample document of that template.</summary>
    private const string SamplePath = "sample.json";

    /// <summary>The consumer every job of this class is accepted for, and the owner of the taken version.</summary>
    private const string Consumer = "consumer-a";

    /// <summary>The record count the composition's orchestration worker applies.</summary>
    private const int SingleBlockRecordLimit = 2_000;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-data-contract",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>The pilot sample's header, as the profile document carries it.</summary>
    private readonly string _sampleHeader;

    /// <summary>The pilot sample's records, in the order the snapshot carries them.</summary>
    private readonly IReadOnlyList<string> _sampleRecords;

    /// <summary>Creates the directory the temporary registry lives in and reads the pilot's sample.</summary>
    public DataContractViolationEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, SamplePath)))!;
        _sampleHeader = sample["header"]!.ToJsonString();
        _sampleRecords = [.. sample["items"]!.AsArray().Select(record => record!.ToJsonString())];
    }

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
    public async Task AHeaderAfterTheItemsEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "header-after-items",
            Encoding.UTF8.GetBytes(
                "{\"contractVersion\":\"1.0\",\"items\":["
                + Record(1, "PLANTED-VALUE-e2e-1-row")
                + "],\"header\":"
                + Header("PLANTED-VALUE-e2e-1-header")
                + "}"),
            "PLANTED-VALUE-e2e-1-header",
            "PLANTED-VALUE-e2e-1-row");

    [Fact]
    public async Task ATruncatedSnapshotEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "truncated",
            TruncatedDocument(),
            "PLANTED-VALUE-e2e-2-header",
            "PLANTED-VALUE-e2e-2-row");

    [Fact]
    public async Task ContentAfterTheClosingEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "content-after-closing",
            Encoding.UTF8.GetBytes(
                "{\"contractVersion\":\"1.0\",\"header\":"
                + Header("PLANTED-VALUE-e2e-3-header")
                + ",\"items\":["
                + Record(1, "PLANTED-VALUE-e2e-3-row")
                + "]}\n{\"PLANTED-NAME-e2e-3\":1}"),
            "PLANTED-VALUE-e2e-3-header",
            "PLANTED-VALUE-e2e-3-row",
            "PLANTED-NAME-e2e-3");

    [Fact]
    public async Task ARecordTheTemplateSchemaRefusesEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "schema-refused-record",
            Document(
                Header("PLANTED-VALUE-e2e-4-header"),
                Record(1, "PLANTED-VALUE-e2e-4-row1"),
                Record(2, "PLANTED-VALUE-e2e-4-row2", quantity: 0),
                Record(3, "PLANTED-VALUE-e2e-4-row3")),
            "PLANTED-VALUE-e2e-4-header",
            "PLANTED-VALUE-e2e-4-row1",
            "PLANTED-VALUE-e2e-4-row2",
            "PLANTED-VALUE-e2e-4-row3");

    [Fact]
    public async Task ARepeatedMemberEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "repeated-member",
            Document(
                Header("PLANTED-VALUE-e2e-5-header"),
                Record(1, "A short description"),
                Duplicated(Record(2, "PLANTED-VALUE-e2e-5-row2"), "PLANTED-NAME-e2e-5"),
                Record(3, "A short description")),
            "PLANTED-VALUE-e2e-5-header",
            "PLANTED-VALUE-e2e-5-row2",
            "PLANTED-NAME-e2e-5");

    [Fact]
    public async Task ARecordTheTemplateSchemaRefusesInTheLastRecordEndsFailedWithTheDataContractCode() =>
        await AssertRefusedAsync(
            "schema-refused-last-record",
            LastRecordSchemaViolationDocument(),
            "PLANTED-VALUE-e2e-6-header",
            "PLANTED-VALUE-e2e-6-row1",
            "PLANTED-VALUE-e2e-6-row2000");

    // One refused job, asserted whole: the status answer says the job ended failed with the
    // data-contract code, its one PDF output failed with the same code and published nothing,
    // and the artifacts listing is empty. Every marker the case planted in its snapshot is
    // denied from the answers the consumer received.
    private async Task AssertRefusedAsync(string name, byte[] document, params string[] markers)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await TakenAsync(connectionString, cancellationToken);

        var (uploadId, uploadBody) = await ReadyUploadAsync(api, document, cancellationToken);
        var (reportJobId, acceptedBody) = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);
        var (artifactIds, artifactsBody) = await ArtifactsAsync(api, reportJobId, cancellationToken);

        Assert.Equal("failed", answer.Status);
        Assert.Equal("DATA_INVALID", answer.ErrorCode);
        Assert.NotNull(answer.FinishedAt);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("failed", output.Status);
        Assert.Equal("DATA_INVALID", output.ErrorCode);
        Assert.Null(output.ArtifactId);
        Assert.Empty(artifactIds);

        foreach (var marker in markers)
        {
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, artifactsBody, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, acceptedBody, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, uploadBody, StringComparison.Ordinal);
        }

        KeepTranscript(name, body);
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);

        // The record count the worker applies is stated here, so the two-thousand-record case
        // is admitted at the block's limit instead of being refused by whatever the deployment
        // happens to carry; every other limit stays as it is.
        var appHost = await TestComposition.CreateAsync(
            cancellationToken,
            $"--Consumers:File={registry}",
            "--Partitioning:SingleBlockRecordLimit=" + SingleBlockRecordLimit.ToString(CultureInfo.InvariantCulture));
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

    // The version the class's jobs are pinned to, in the composition's own database and storage
    // root: the pilot's schema, packaged through the archive a take uses, written under the
    // content-addressed key of its bytes and recorded with the written package's size, hash and
    // file count. The data step reads the schema from that package before any page is rendered.
    private static async Task TakenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var schema = await File.ReadAllBytesAsync(
            Path.Combine(RepositoryPaths.Root, "samples", "templates", Template, SchemaPath),
            cancellationToken);
        var files = new List<PackageFileSource>
        {
            new(SchemaPath, _ => Task.FromResult<Stream>(new MemoryStream(schema, writable: false))),
        };

        using var sink = new MemoryStream();
        await TemplatePackageArchive.WriteAsync(files, sink, cancellationToken);
        var bytes = sink.ToArray();
        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = TemplatePackageKeys.ForVersion(Consumer, Template, Version, packageSha256);

        var root = StorageRoot();
        Directory.CreateDirectory(root);
        var objects = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = root, CreateIfMissing = true },
            root,
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        using (var content = new MemoryStream(bytes, writable: false))
        {
            var written = await objects.WriteAsync(key, content, cancellationToken);
            if (written.Succeeded)
            {
                // The object was stored under the key its bytes name.
            }
            else if (written.Outcome == StorageOutcome.AlreadyExists)
            {
                // The key's last segment is the package's own SHA-256, so an object already there
                // is this package: a repeat write is the no-op the store answers. It is read back
                // and verified rather than trusted.
                await VerifyStoredPackageAsync(objects, key, bytes, packageSha256, cancellationToken);
            }
            else
            {
                Assert.Fail(
                    $"the taken package had to be written: the store answered {written.Outcome} "
                    + $"({written.Error?.Code ?? "no code"}: {written.Error?.Message ?? "no message"})");
            }
        }

        var record = TemplateVersionRecords.Sample(
                Template,
                Version,
                Consumer,
                shared: true,
                schemaRef: SchemaPath,
                packageSha256: packageSha256) with
        {
            PackageSize = bytes.Length,
            FileCount = files.Count,
            ObjectKey = key,
        };
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var versions = new PostgresTemplateVersionStore(dataSource);
        var inserted = await versions.TryInsertAsync(record, cancellationToken);
        Assert.True(inserted.WasInserted, "the taken version had to be recorded in the composition's store");
    }

    // Reads the object at the content-addressed key back through the verifying reader, which
    // checks the size and the SHA-256 as the content is consumed, so a repeat write continues
    // only when the object is the package this class built.
    private static async Task VerifyStoredPackageAsync(
        IObjectStore objects,
        ObjectKey key,
        byte[] bytes,
        string packageSha256,
        CancellationToken cancellationToken)
    {
        var read = await objects.OpenReadAsync(key, bytes.Length, packageSha256, cancellationToken);
        if (!read.Succeeded || read.Value is null)
        {
            Assert.Fail(
                $"the object at the taken key had to be read back and verified: the store answered {read.Outcome} "
                + $"({read.Error?.Code ?? "no code"}: {read.Error?.Message ?? "no message"}); "
                + $"{bytes.Length} bytes with SHA-256 {packageSha256} were expected");
        }

        await using var verified = read.Value!;
        await verified.Content.CopyToAsync(Stream.Null, cancellationToken);
    }

    // A snapshot is stored through the product's upload path and confirmed. The confirmation
    // does not read the document's contract, so a hostile snapshot ends ready and reaches a
    // job; a snapshot refused here would never reach the step this class asserts.
    private async Task<(string UploadId, string Body)> ReadyUploadAsync(
        HttpClient api,
        byte[] document,
        CancellationToken cancellationToken)
    {
        using var created = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
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
                    Headers = { { ConsumerIdentity.HeaderName, Consumer } },
                },
                cancellationToken);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var confirmed = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        var found = JsonDocument.Parse("{}").RootElement.Clone();
        await Wait.UntilAsync(
            $"the upload {uploadId} to end ready",
            async token =>
            {
                using var read = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                read.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
                using var response = await api.SendAsync(read, token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return false;
                }

                using var projection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                found = projection.RootElement.Clone();
                return found.GetProperty("status").GetString() is "ready" or "rejected";
            },
            ReadyDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        if (found.GetProperty("status").GetString() == "rejected")
        {
            Assert.Fail(
                "the snapshot was refused at its confirmation, so it never reached a job: "
                + found.GetProperty("rejectionCode").GetString()
                + "/"
                + found.GetProperty("rejectionReason").GetString());
        }

        return (uploadId, found.GetRawText());
    }

    private static async Task<(string ReportJobId, string Body)> AcceptJobAsync(
        HttpClient api,
        string uploadId,
        CancellationToken cancellationToken)
    {
        using var accepted = await api.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent(
                    $$"""{"template":{"id":"{{Template}}","version":"{{Version}}"},"data":{"uploadId":"{{uploadId}}","profile":"header-items-v1"},"outputs":["pdf"]}""",
                    Encoding.UTF8,
                    "application/json"),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        var body = await accepted.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("queued", document.RootElement.GetProperty("status").GetString());
        return (document.RootElement.GetProperty("reportJobId").GetString()!, body);
    }

    // The status read, polled until the job ends: the answer and the body it came in, so the
    // transcript kept with the run is what the service actually said.
    private async Task<(Answer Answer, string Body)> WaitForTerminalAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        (Answer Answer, string Body)? last = null;
        await Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token =>
            {
                last = await ReadAsync(api, reportJobId, token);
                return last.Value.Answer.Status is "failed" or "succeeded" or "cancelled";
            },
            TerminalDeadline,
            PollInterval,
            cancellationToken);
        return last!.Value;
    }

    private static async Task<(Answer Answer, string Body)> ReadAsync(
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
        Assert.True(response.Headers.CacheControl?.NoStore, "every status answer has to be uncacheable");

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return (
            new Answer(
                root.GetProperty("status").GetString()!,
                root.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorCode").GetString(),
                root.GetProperty("finishedAt") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("finishedAt").GetString(),
                [
                    .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                        element.GetProperty("format").GetString()!,
                        element.GetProperty("status").GetString()!,
                        element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString(),
                        element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString())),
                ]),
            body);
    }

    // The job's artifacts as its owner lists them: a refused job publishes none.
    private static async Task<(IReadOnlyList<string> ArtifactIds, string Body)> ArtifactsAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}/artifacts", UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return (
            [.. document.RootElement.GetProperty("artifacts").EnumerateArray()
                .Select(artifact => artifact.GetProperty("artifactId").GetString()!)],
            body);
    }

    // The pilot's own header with its title carrying the case's value marker: the taken schema
    // requires a string there and admits the sample's own members, so the header every document
    // carries is the sample's with that one value replaced.
    private string Header(string reportTitle)
    {
        var header = JsonNode.Parse(_sampleHeader)!.AsObject();
        header["reportTitle"] = reportTitle;
        return header.ToJsonString();
    }

    // One record of the case, built from the pilot's own records so every member the schema
    // requires is present and valid; the description carries the case's marker and the
    // quantity is replaced only by the cases that break the schema on purpose.
    private string Record(int ordinal, string description, int? quantity = null)
    {
        var record = JsonNode.Parse(_sampleRecords[(ordinal - 1) % _sampleRecords.Count])!.AsObject();
        record["sequence"] = ordinal;
        record["description"] = description;
        if (quantity is { } value)
        {
            record["quantity"] = value;
        }

        return record.ToJsonString();
    }

    private static byte[] Document(string header, params string[] records) =>
        Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":"
            + header
            + ",\"items\":["
            + string.Join(',', records)
            + "]}");

    // The pilot's document cut inside its second record: the header is whole and valid, the
    // first record is whole, and the pass stops where the document ends.
    private byte[] TruncatedDocument()
    {
        var second = Record(2, "PLANTED-VALUE-e2e-2-row");
        return Encoding.UTF8.GetBytes(
            "{\"contractVersion\":\"1.0\",\"header\":"
            + Header("PLANTED-VALUE-e2e-2-header")
            + ",\"items\":["
            + Record(1, "A short description")
            + ","
            + second[..(second.Length / 2)]);
    }

    // The pilot's document with one member of its last record repeated: the reader meets the
    // repetition while it captures that record, before the schema is asked about it.
    private static string Duplicated(string json, string member) =>
        json[..^1] + ",\"" + member + "\":1,\"" + member + "\":2}";

    // Two thousand valid records and a last one the schema refuses: the pass reaches the end
    // of the snapshot before it stops.
    private byte[] LastRecordSchemaViolationDocument()
    {
        var records = new List<string>(SingleBlockRecordLimit);
        records.Add(Record(1, "PLANTED-VALUE-e2e-6-row1"));
        for (var ordinal = 2; ordinal < SingleBlockRecordLimit; ordinal++)
        {
            records.Add(Record(ordinal, "A short description"));
        }

        records.Add(Record(SingleBlockRecordLimit, "PLANTED-VALUE-e2e-6-row2000", quantity: 0));
        return Document(Header("PLANTED-VALUE-e2e-6-header"), [.. records]);
    }

    // The demo transcript: the answer, as the service wrote it, kept with the run when the run
    // has a folder of its own.
    private static void KeepTranscript(string name, string body)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"no {RunEnvironment.RunDirectoryVariable}: the transcript was not written");
            return;
        }

        var path = RunEnvironment.WriteEvidence(
            "data-contract-violation." + name + ".json",
            Encoding.UTF8.GetBytes(body));
        TestContext.Current.TestOutputHelper?.WriteLine($"transcript: {path}");
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    /// <summary>One answer of the status read, as the case sampled it.</summary>
    private sealed record Answer(
        string Status,
        string? ErrorCode,
        string? FinishedAt,
        IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of an answer.</summary>
    private sealed record OutputAnswer(
        string Format,
        string Status,
        string? ErrorCode,
        string? ArtifactId);
}
