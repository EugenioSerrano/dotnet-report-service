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
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.TemplateSource.Manifest;
using ReportService.Testing;
using ReportService.Testing.Limits;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The single block's limit, followed the way a consumer follows its report: a snapshot is
/// uploaded and confirmed against the running composition, the job is accepted, and the status
/// read is polled until it ends. A snapshot one record over the configured limit answers
/// <c>failed</c> with the quota code and the message that names the limit its data exceeded, on
/// the job and on its PDF output, with no artifact to download; a snapshot at the limit answers
/// <c>succeeded</c> with one artifact whose bytes verify by size and SHA-256 and no message. The
/// answers of both cases are kept as the demo transcript.
/// </summary>
public sealed class SingleBlockLimitEndToEndTests : IDisposable
{
    private const string Registry = """
        { "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }
        """;

    /// <summary>The template every job of this class names.</summary>
    private const string Template = "pilot-simple";

    /// <summary>The version every job of this class names.</summary>
    private const string Version = "1.0.0";

    /// <summary>The consumer every job of this class is accepted for, and the owner of the taken version.</summary>
    private const string Consumer = "consumer-a";

    /// <summary>The record count the composition's orchestration worker applies.</summary>
    private const int Limit = 1_000;

    /// <summary>The sentence a refused producer reads: the applied limit and the record that crossed it.</summary>
    public const string RefusalSentence =
        "The PDF report was not printed: record 1001 exceeds this version's limit of 1000 records per report (one block). Send a smaller report.";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-limit",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public SingleBlockLimitEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task ASnapshotOverTheLimitAnswersFailedWithTheSentenceAndNothingToDownload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await TakenAsync(connectionString, cancellationToken);

        var uploadId = await ReadyUploadAsync(api, LimitSnapshots.RecordsOverTheBound(), cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        Assert.Equal("failed", answer.Status);
        Assert.Equal("QUOTA_EXCEEDED", answer.ErrorCode);
        Assert.Equal(RefusalSentence, answer.ErrorMessage);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("failed", output.Status);
        Assert.Equal("QUOTA_EXCEEDED", output.ErrorCode);
        Assert.Equal(RefusalSentence, output.ErrorMessage);
        Assert.Null(output.ArtifactId);
        Assert.Empty(await ArtifactIdsAsync(api, reportJobId, cancellationToken));

        KeepTranscript("refused", body);
    }

    [Fact]
    public async Task ASnapshotAtTheLimitAnswersSucceededWithOneVerifiedArtifactAndNoMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await TakenAsync(connectionString, cancellationToken);

        var uploadId = await ReadyUploadAsync(api, LimitSnapshots.RecordsAtBound(), cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        Assert.Equal("succeeded", answer.Status);
        Assert.Null(answer.ErrorCode);
        Assert.Equal(JsonValueKind.Null, answer.ErrorMessageKind);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("succeeded", output.Status);
        Assert.NotNull(output.ArtifactId);
        Assert.Equal(JsonValueKind.Null, output.ErrorMessageKind);

        // The one artifact is the one the listing answers, and its object verifies by size and
        // SHA-256 through the storage port.
        var listed = Assert.Single(await ArtifactsAsync(api, reportJobId, cancellationToken));
        Assert.Equal(output.ArtifactId, listed.GetProperty("artifactId").GetString());
        var size = listed.GetProperty("sizeBytes").GetInt64();
        var sha256 = listed.GetProperty("sha256").GetString()!;

        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = false },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        var objectKey = await ObjectKeyOfAsync(connectionString, reportJobId, cancellationToken);
        var read = await storage.OpenReadAsync(ObjectKey.Create(objectKey), size, sha256, cancellationToken);
        Assert.True(read.Succeeded, $"the artifact of {reportJobId} had to verify through the storage port");
        await using var content = read.Value!;
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, cancellationToken);
        Assert.Equal(size, buffer.Length);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));

        KeepTranscript("printed", body);
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);

        // The record count the worker applies is stated here, so the case asserts the limit it
        // configured rather than whatever the deployment happens to carry.
        var appHost = await TestComposition.CreateAsync(
            cancellationToken,
            $"--Consumers:File={registry}",
            "--Partitioning:SingleBlockRecordLimit=" + Limit.ToString(CultureInfo.InvariantCulture));
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

    // The version the class's jobs are pinned to: the sample template, published into the
    // composition's own storage root and taken here with the product's own take, so the version's
    // bytes and its record are the ones every process of the composition reads.
    private static async Task TakenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            NullLogger<FilesystemObjectStore>.Instance);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var versions = new PostgresTemplateVersionStore(dataSource);

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
        Assert.Equal(Consumer, outcome.Record.OwnerConsumerId);
    }

    // The sample template's own package, with the corrections these cases need: this
    // composition's consumer as the owner and one named font family for the print.
    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "samples", "templates", Template);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
            }

            if (string.Equals(relative, "html/styles.css", StringComparison.Ordinal))
            {
                text = text.Replace("font-family: sans-serif;", "font-family: \"Liberation Sans\";", StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    private async Task<string> ReadyUploadAsync(HttpClient api, byte[] document, CancellationToken cancellationToken)
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

        await Wait.UntilAsync(
            $"the upload {uploadId} to end ready",
            async token =>
            {
                using var read = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
                read.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
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

    private static async Task<string> AcceptJobAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
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
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("reportJobId").GetString()!;
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
                root.GetProperty("errorMessage").ValueKind,
                root.GetProperty("errorMessage") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorMessage").GetString(),
                [
                    .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                        element.GetProperty("format").GetString()!,
                        element.GetProperty("status").GetString()!,
                        element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString(),
                        element.GetProperty("errorMessage").ValueKind,
                        element.GetProperty("errorMessage") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorMessage").GetString(),
                        element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString())),
                ]),
            body);
    }

    private static async Task<IReadOnlyList<string>> ArtifactIdsAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken) =>
        [.. (await ArtifactsAsync(api, reportJobId, cancellationToken))
            .Select(artifact => artifact.GetProperty("artifactId").GetString()!)];

    private static async Task<IReadOnlyList<JsonElement>> ArtifactsAsync(
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
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return [.. document.RootElement.GetProperty("artifacts").EnumerateArray().Select(artifact => artifact.Clone())];
    }

    private static async Task<string> ObjectKeyOfAsync(
        string connectionString,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.object_key FROM report_service.artifacts a WHERE a.report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
        var key = await command.ExecuteScalarAsync(cancellationToken);
        Assert.IsType<string>(key);
        return (string)key!;
    }

    // The demo transcript: both answers, as the service wrote them, kept with the run when the
    // run has a folder of its own.
    private static void KeepTranscript(string name, string body)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"no {RunEnvironment.RunDirectoryVariable}: the transcript was not written");
            return;
        }

        var path = RunEnvironment.WriteEvidence(
            "single-block-limit." + name + ".json",
            Encoding.UTF8.GetBytes(body));
        TestContext.Current.TestOutputHelper?.WriteLine($"transcript: {path}");
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record Answer(
        string Status,
        string? ErrorCode,
        JsonValueKind ErrorMessageKind,
        string? ErrorMessage,
        IReadOnlyList<OutputAnswer> Outputs);

    private sealed record OutputAnswer(
        string Format,
        string Status,
        string? ErrorCode,
        JsonValueKind ErrorMessageKind,
        string? ErrorMessage,
        string? ArtifactId);
}
