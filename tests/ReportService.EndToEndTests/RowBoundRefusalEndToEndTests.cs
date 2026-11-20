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
using ReportService.Api.Uploads;
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
/// The PDF row bound, followed the way a consumer follows its report: a snapshot is uploaded and
/// confirmed against the running composition, the job is accepted, and the status read is polled
/// through the real HTTP pipeline until it ends. A document whose description is above 262 144
/// bytes of UTF-8 answers <c>failed</c> with the row-bound code and a message naming the record,
/// the field, its size and the bound - the same sentence on the job and on its PDF output - with
/// no artifact to download and no part of the field's value in the response; the first offending
/// record is the one reported.
/// </summary>
public sealed class RowBoundRefusalEndToEndTests : IDisposable
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

    /// <summary>The record count the composition's orchestration worker applies, above every snapshot here.</summary>
    private const int SingleBlockRecordLimit = 2_000;

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-row-bound",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public RowBoundRefusalEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task ADescriptionAboveTheBoundAnswersFailedWithTheSentenceAndNothingToDownload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        await TakenAsync(await ConnectionStringAsync(app, cancellationToken), cancellationToken);

        var value = UpperCase(262_145);
        var uploadId = await ReadyUploadAsync(
            api,
            Snapshot(1_500, ordinal => ordinal == 1_500 ? value : "A short description"),
            cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        await AssertRefusedAsync(api, reportJobId, answer, body, ordinal: 1_500, size: 262_145, value, cancellationToken);
        KeepTranscript("field-over-the-bound", body);
    }

    [Fact]
    public async Task OneHundredThousandThreeByteCharactersAnswerWithTheirSizeInBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        await TakenAsync(await ConnectionStringAsync(app, cancellationToken), cancellationToken);

        var value = new string('\u20AC', 100_000);
        var uploadId = await ReadyUploadAsync(
            api,
            Snapshot(3, ordinal => ordinal == 3 ? value : "A short description"),
            cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        await AssertRefusedAsync(api, reportJobId, answer, body, ordinal: 3, size: 300_000, value, cancellationToken);
        KeepTranscript("three-byte-characters", body);
    }

    [Fact]
    public async Task TheFirstOfThreeOversizedRecordsIsTheRecordTheMessageNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        await TakenAsync(await ConnectionStringAsync(app, cancellationToken), cancellationToken);

        var value = UpperCase(262_145);
        var uploadId = await ReadyUploadAsync(
            api,
            Snapshot(9, ordinal => ordinal is 2 or 5 or 9 ? value : "A short description"),
            cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        await AssertRefusedAsync(api, reportJobId, answer, body, ordinal: 2, size: 262_145, value, cancellationToken);
        KeepTranscript("first-of-three", body);
    }

    // One refused answer, asserted whole: the code and the sentence on the job and on its output,
    // no artifact, and no part of the field's value anywhere in the body the consumer received.
    private static async Task AssertRefusedAsync(
        HttpClient api,
        string reportJobId,
        Answer answer,
        string body,
        int ordinal,
        long size,
        string value,
        CancellationToken cancellationToken)
    {
        Assert.Equal("failed", answer.Status);
        Assert.Equal("ROW_BOUND_EXCEEDED", answer.ErrorCode);
        Assert.NotNull(answer.ErrorMessage);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("failed", output.Status);
        Assert.Equal("ROW_BOUND_EXCEEDED", output.ErrorCode);
        Assert.Equal(answer.ErrorMessage, output.ErrorMessage);
        Assert.Null(output.ArtifactId);
        Assert.Empty(await ArtifactIdsAsync(api, reportJobId, cancellationToken));

        // The sentence names the record, the field, its size and the bound, and no part of the
        // value: every window of sixteen characters of the field is looked for in the body.
        var message = answer.ErrorMessage!;
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"Record {ordinal} has a text field at $.description of {size} bytes of UTF-8"),
            message,
            StringComparison.Ordinal);
        Assert.Contains("262144", message, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAA", message, StringComparison.Ordinal);
        for (var start = 0; start + 16 <= value.Length; start += 8)
        {
            Assert.DoesNotContain(value.Substring(start, 16), body, StringComparison.Ordinal);
        }
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry, cancellationToken);

        // The record count the worker applies is stated here, so the row bound is the first rule
        // these snapshots can cross; every other delivered limit stays as it is.
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

    // The version the class's jobs are pinned to: the sample template, published into the
    // composition's own storage root and taken here with the product's own take.
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

    // The sample template's own package, with this composition's consumer as the owner.
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

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    // The pilot's own document, with the descriptions the case states: every other field comes
    // from the template's sample, so the schema admits every record and the row bound is the
    // only rule that can refuse it.
    private static byte[] Snapshot(int records, Func<int, string> description)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "samples", "templates", Template, "sample.json")))!;
        var prototype = sample["items"]!.AsArray();
        var items = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(prototype[index % prototype.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;
            item["description"] = description(index + 1);
            items.Add(item);
        }

        sample["items"] = items;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private static string UpperCase(int length)
    {
        var random = new Random(20261119);
        var value = new char[length];
        for (var index = 0; index < length; index++)
        {
            value[index] = (char)('A' + random.Next(26));
        }

        return new string(value);
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
                root.GetProperty("errorMessage") is { ValueKind: JsonValueKind.Null } ? null : root.GetProperty("errorMessage").GetString(),
                [
                    .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                        element.GetProperty("format").GetString()!,
                        element.GetProperty("status").GetString()!,
                        element.GetProperty("errorCode") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorCode").GetString(),
                        element.GetProperty("errorMessage") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("errorMessage").GetString(),
                        element.GetProperty("artifactId") is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty("artifactId").GetString())),
                ]),
            body);
    }

    private static async Task<IReadOnlyList<string>> ArtifactIdsAsync(
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
        return [.. document.RootElement.GetProperty("artifacts").EnumerateArray()
            .Select(artifact => artifact.GetProperty("artifactId").GetString()!)];
    }

    // The demo transcript: the answer, as the service wrote it, kept with the run.
    private static void KeepTranscript(string name, string body)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"no {RunEnvironment.RunDirectoryVariable}: the transcript was not written");
            return;
        }

        var path = RunEnvironment.WriteEvidence(
            "row-bound-refusal." + name + ".json",
            Encoding.UTF8.GetBytes(body));
        TestContext.Current.TestOutputHelper?.WriteLine($"transcript: {path}");
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record Answer(
        string Status,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<OutputAnswer> Outputs);

    private sealed record OutputAnswer(
        string Format,
        string Status,
        string? ErrorCode,
        string? ErrorMessage,
        string? ArtifactId);
}
