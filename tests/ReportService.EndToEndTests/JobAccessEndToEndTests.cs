using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The whole demonstration over the local composition, with the registry rewritten under the
/// running API: consumer A uploads a snapshot, its job is printed, and its own status read, list
/// and download answer as they should; consumer B gets, for A's job and file, the same answer as
/// for identifiers that do not exist; a request with no identity, a malformed one or an
/// unregistered one gets the one CONSUMER_UNKNOWN answer on the three reads; A made inactive by a
/// registry rewrite still reads, lists and downloads exactly as before; A removed is refused and,
/// added back, reads its data again — no request in between waiting longer than the registry's own
/// bound. The transcript of this class is the demo's record.
/// </summary>
[Trait("Requires", "printing")]
public sealed class JobAccessEndToEndTests : IDisposable
{
    private const string ConsumerA = "consumer-a";

    private const string ConsumerB = "consumer-b";

    private const string ConsumerC = "consumer-c";

    /// <summary>The template the job of this class is accepted for.</summary>
    private const string Template = "pilot-access";

    /// <summary>The shipped sample the package is built from.</summary>
    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PublishedDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ChangeBound = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-access",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public JobAccessEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task TheThreeJobReadsAnswerOnlyTheirConsumerAcrossRegistryChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, RegistryBody(activeA: true, includeA: true), cancellationToken);
        await using var composition = await StartAsync(registry, cancellationToken);
        var processId = await ProcessIdAsync(composition, cancellationToken);
        using var api = composition.CreateHttpClient("api");
        var connectionString = await composition.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        await TakenAsync(dataSource, new PostgresTemplateVersionStore(dataSource), storage, cancellationToken);

        // Step 1: A uploads, confirms, creates a job, waits for it, lists and downloads its file.
        var uploadId = await ReadyUploadAsync(api, ConsumerA, Snapshot(25), cancellationToken);
        var jobId = await SubmitAsync(api, uploadId, cancellationToken);
        var status = await WaitForSucceededAsync(api, jobId, cancellationToken);
        var listed = Assert.Single((await ListArtifactsAsync(api, jobId, ConsumerA, cancellationToken)).Artifacts);
        var baselineStatus = await ReadBodyAsync(api, StatusRequest(ConsumerA, jobId), cancellationToken);
        var baselineList = await ReadBodyAsync(api, ListRequest(ConsumerA, jobId), cancellationToken);
        byte[] baselineFile;
        using (var download = await DownloadAsync(api, ConsumerA, jobId, listed.ArtifactId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            baselineFile = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(listed.SizeBytes, baselineFile.LongLength);
            Assert.Equal(listed.Sha256, HashOf(baselineFile));
        }

        // Step 2: B gets, for A's job and file, the same answer as for identifiers that do not exist.
        // Each operation is compared with its own absent answer: a job that does not exist is the
        // status read's answer, a file that does not exist is the download's, and the listing's
        // not-found is the status read's own document. The status and the download carry different
        // text by design, so they are never compared with each other.
        var jobNotFound = await ReadProblemAsync(api, ConsumerB, StatusRequest(ConsumerB, jobId), cancellationToken);
        var absentJob = await ReadProblemAsync(
            api,
            ConsumerB,
            StatusRequest(ConsumerB, "job-" + new string('f', 32)),
            cancellationToken);
        var foreignList = await ReadProblemAsync(api, ConsumerB, ListRequest(ConsumerB, jobId), cancellationToken);
        var absentList = await ReadProblemAsync(
            api,
            ConsumerB,
            ListRequest(ConsumerB, "job-" + new string('f', 32)),
            cancellationToken);
        var absentFile = await ReadProblemAsync(
            api,
            ConsumerB,
            DownloadRequest(ConsumerB, "job-" + new string('f', 32), "artifact-" + new string('f', 32)),
            cancellationToken);
        var foreignFile = await ReadProblemAsync(
            api,
            ConsumerB,
            DownloadRequest(ConsumerB, jobId, listed.ArtifactId),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, jobNotFound.StatusCode);
        Assert.Equal("INPUT_NOT_FOUND", jobNotFound.Code);
        Assert.Equal(ReportJobStatusEndpoint.NotFoundTitle, jobNotFound.Title);
        Assert.Equal(ReportJobStatusEndpoint.NotFoundDetail, jobNotFound.Detail);
        Assert.Equal(jobNotFound with { TraceId = null }, absentJob with { TraceId = null });

        // The listing answers the job status read's not-found document (the approved shared answer),
        // and B's request for an absent job's listing is that same answer.
        Assert.Equal(HttpStatusCode.NotFound, foreignList.StatusCode);
        Assert.Equal("INPUT_NOT_FOUND", foreignList.Code);
        Assert.Equal(jobNotFound with { TraceId = null }, foreignList with { TraceId = null });
        Assert.Equal(foreignList with { TraceId = null }, absentList with { TraceId = null });

        // The download answers its own not-found document, to B on A's job and file exactly as to
        // an absent job and an absent file; it is never compared with the status read's answer.
        Assert.Equal(HttpStatusCode.NotFound, absentFile.StatusCode);
        Assert.Equal("INPUT_NOT_FOUND", absentFile.Code);
        Assert.Equal(ArtifactDownloadEndpoint.NotFoundTitle, absentFile.Title);
        Assert.Equal(ArtifactDownloadEndpoint.NotFoundDetail, absentFile.Detail);
        Assert.Equal(absentFile with { TraceId = null }, foreignFile with { TraceId = null });

        // Step 3: no identity, a malformed identity and an unregistered one on the three reads.
        var refusals = new List<Problem>();
        foreach (var declared in new[] { (string?)null, "consumer a", "consumer-z" })
        {
            foreach (var path in new[]
                     {
                         StatusPath(jobId),
                         ListPath(jobId),
                         DownloadPath(jobId, listed.ArtifactId),
                     })
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
                if (declared is not null)
                {
                    request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, declared);
                }

                refusals.Add(await ReadProblemAsync(api, request, cancellationToken));
            }
        }

        Assert.All(refusals, refusal =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
            Assert.Equal("CONSUMER_UNKNOWN", refusal.Code);
            Assert.Equal(ConsumerAccess.UnknownTitle, refusal.Title);
        });
        var oneRefusal = refusals[0] with { TraceId = null };
        Assert.All(refusals, refusal => Assert.Equal(oneRefusal, refusal with { TraceId = null }));

        // Step 4: A made inactive by the running registry: every answer is the one it gave while active.
        var fingerprint = await FingerprintAsync(api, cancellationToken);
        await File.WriteAllTextAsync(registry, RegistryBody(activeA: false, includeA: true), cancellationToken);
        await Wait.UntilAsync(
            "the registry to make consumer A inactive",
            async token => await FingerprintAsync(api, token) != fingerprint,
            ChangeBound,
            PollInterval,
            cancellationToken);

        Assert.Equal(baselineStatus, await ReadBodyAsync(api, StatusRequest(ConsumerA, jobId), cancellationToken));
        Assert.Equal(baselineList, await ReadBodyAsync(api, ListRequest(ConsumerA, jobId), cancellationToken));
        using (var download = await DownloadAsync(api, ConsumerA, jobId, listed.ArtifactId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(baselineFile, await download.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        // Step 5: A removed is the step-3 refusal; added back, every answer returns.
        var inactiveFingerprint = await FingerprintAsync(api, cancellationToken);
        await File.WriteAllTextAsync(registry, RegistryBody(activeA: true, includeA: false), cancellationToken);
        await Wait.UntilAsync(
            "the registry to remove consumer A",
            async token => await FingerprintAsync(api, token) != inactiveFingerprint,
            ChangeBound,
            PollInterval,
            cancellationToken);
        Assert.Equal(
            oneRefusal,
            await ReadProblemAsync(api, ConsumerA, StatusRequest(ConsumerA, jobId), cancellationToken) with { TraceId = null });

        var removedFingerprint = await FingerprintAsync(api, cancellationToken);
        await File.WriteAllTextAsync(registry, RegistryBody(activeA: true, includeA: true), cancellationToken);
        await Wait.UntilAsync(
            "the registry to add consumer A back",
            async token => await FingerprintAsync(api, token) != removedFingerprint,
            ChangeBound,
            PollInterval,
            cancellationToken);
        Assert.Equal(baselineStatus, await ReadBodyAsync(api, StatusRequest(ConsumerA, jobId), cancellationToken));
        Assert.Equal(baselineList, await ReadBodyAsync(api, ListRequest(ConsumerA, jobId), cancellationToken));

        // Step 6: the service stayed ready throughout and the API was never restarted.
        using (var health = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", health.RootElement.GetProperty("status").GetString());
        }

        Assert.Equal(processId, await ProcessIdAsync(composition, cancellationToken));

        var transcript =
            $"job {jobId}: consumer-a succeeded, listed 1 artifact {listed.ArtifactId} ({listed.SizeBytes} bytes, sha256 {listed.Sha256}) and downloaded it; "
            + "consumer-b got 404 for the job, the list and the file, equal to absent identifiers; "
            + $"{refusals.Count} refusals (missing, malformed, unregistered) were one 403 CONSUMER_UNKNOWN answer; "
            + "consumer-a inactive read, listed and downloaded the same; removed, it was refused; added back, it read its data again.";
        TestContext.Current.TestOutputHelper?.WriteLine(transcript);
        RunEnvironment.WriteEvidence("job-access-demo.txt", Encoding.UTF8.GetBytes(transcript + Environment.NewLine));
    }

    private static async Task<DistributedApplication> StartAsync(string registry, CancellationToken cancellationToken)
    {
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

    private static string RegistryBody(bool activeA, bool includeA) =>
        "{ \"consumers\": ["
        + string.Join(
            ", ",
            new[]
            {
                includeA ? $"{{ \"consumerId\": \"{ConsumerA}\", \"displayName\": \"Consumer A\", \"active\": {(activeA ? "true" : "false")} }}" : null,
                $"{{ \"consumerId\": \"{ConsumerB}\", \"displayName\": \"Consumer B\", \"active\": true }}",
                $"{{ \"consumerId\": \"{ConsumerC}\", \"displayName\": \"Consumer C\", \"active\": false }}",
            }.OfType<string>())
        + "] }";

    private static async Task<string> FingerprintAsync(HttpClient api, CancellationToken cancellationToken)
    {
        using var body = JsonDocument.Parse(await api.GetStringAsync(
            new Uri(ConsumerRegistryHosting.DiagnosticPath, UriKind.Relative),
            cancellationToken));
        return body.RootElement.GetProperty("checks")[0].GetProperty("data").GetProperty("fingerprint").GetString()!;
    }

    private const string ProcessIdProperty = "executable.pid";

    private static async Task<string> ProcessIdAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var resource = await app.ResourceNotifications.WaitForResourceAsync(
            "api",
            e => e.Snapshot.Properties.Any(p => p.Name == ProcessIdProperty && p.Value is not null),
            cancellationToken);
        return Convert.ToString(resource.Snapshot.Properties.Single(p => p.Name == ProcessIdProperty).Value, CultureInfo.InvariantCulture)!;
    }

    private static HttpRequestMessage StatusRequest(string consumer, string jobId) =>
        Request(StatusPath(jobId), consumer);

    private static HttpRequestMessage ListRequest(string consumer, string jobId) =>
        Request(ListPath(jobId), consumer);

    private static HttpRequestMessage DownloadRequest(string consumer, string jobId, string artifactId) =>
        Request(DownloadPath(jobId, artifactId), consumer);

    private static HttpRequestMessage Request(string path, string consumer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        return request;
    }

    private static string StatusPath(string jobId) => $"{CreateReportJobEndpoint.RoutePrefix}/{jobId}";

    private static string ListPath(string jobId) => $"{StatusPath(jobId)}{ArtifactListEndpoint.RouteSuffix}";

    private static string DownloadPath(string jobId, string artifactId) =>
        $"{ListPath(jobId)}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}";

    private static async Task<string> ReadBodyAsync(
        HttpClient api,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await api.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Task<Problem> ReadProblemAsync(
        HttpClient api,
        string consumer,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // The request the caller built already declares its identity; the consumer names the case.
        _ = consumer;
        return ReadProblemAsync(api, request, cancellationToken);
    }

    private static async Task<Problem> ReadProblemAsync(
        HttpClient api,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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

    private static async Task<Listing> ListArtifactsAsync(
        HttpClient api,
        string reportJobId,
        string consumer,
        CancellationToken cancellationToken)
    {
        using var response = await api.SendAsync(ListRequest(consumer, reportJobId), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var artifacts = new List<ListedArtifact>();
        foreach (var artifact in document.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            artifacts.Add(new ListedArtifact(
                artifact.GetProperty("artifactId").GetString()!,
                artifact.GetProperty("format").GetString()!,
                artifact.GetProperty("sizeBytes").GetInt64(),
                artifact.GetProperty("sha256").GetString()!));
        }

        return new Listing(document.RootElement.GetProperty("reportJobId").GetString()!, artifacts);
    }

    private static Task<HttpResponseMessage> DownloadAsync(
        HttpClient api,
        string consumer,
        string jobId,
        string artifactId,
        CancellationToken cancellationToken) =>
        api.SendAsync(
            DownloadRequest(consumer, jobId, artifactId),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

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
        Assert.True(
            string.Equals(answer!.Status, "succeeded", StringComparison.Ordinal),
            $"the job had to succeed for the download to be demonstrated; it ended {answer.Status}");
        return answer;
    }

    private static async Task<StatusAnswer> ReadStatusAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        using var response = await api.SendAsync(StatusRequest(ConsumerA, reportJobId), cancellationToken);
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

    // A ready upload of the snapshot, made through the API's own upload operations.
    private static async Task<string> ReadyUploadAsync(
        HttpClient api,
        string consumer,
        byte[] document,
        CancellationToken cancellationToken)
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
                using var read = Request($"{UploadEndpoints.RoutePrefix}/{uploadId}", consumer);
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
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, ConsumerA);
        using var response = await api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    // The version the job prints, taken in the test process with the product's own take: the taken
    // bytes reach the composition's shared storage root and its record the metadata database.
    private async Task TakenAsync(
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

        var outcome = await take.TakeAsync(Template, Version, ConsumerA, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(Version, outcome.Record!.Version);
    }

    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Sample);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + ConsumerA, StringComparison.Ordinal);
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

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private static string HashOf(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>One answer of the listing.</summary>
    private sealed record Listing(string ReportJobId, IReadOnlyList<ListedArtifact> Artifacts);

    /// <summary>One artifact the listing carries.</summary>
    private sealed record ListedArtifact(string ArtifactId, string Format, long SizeBytes, string Sha256);

    /// <summary>One answer of the job status read.</summary>
    private sealed record StatusAnswer(string Status, IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of a job's answer.</summary>
    private sealed record OutputAnswer(string Format, string Status, string? ArtifactId);

    /// <summary>One problem answer as the demonstration compared it.</summary>
    private sealed record Problem(HttpStatusCode StatusCode, string? Code, string? Title, string? Detail, string? TraceId);
}
