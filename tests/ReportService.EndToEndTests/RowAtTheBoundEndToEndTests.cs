using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Uploads;
using ReportService.Pdf.Verification;
using ReportService.Storage;
using ReportService.Storage.Filesystem;
using ReportService.TemplatePackages.Records;
using ReportService.TemplateSource;
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.Fixtures;
using ReportService.Testing.Reading;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;

namespace ReportService.EndToEndTests;

/// <summary>
/// The accepting side of the PDF row bound, followed the way a consumer follows its report: a
/// snapshot is uploaded and confirmed against the running composition, the job is accepted, and
/// the status read is polled through the real HTTP pipeline until it succeeds. The consumer then
/// lists the job's artifacts, downloads the one PDF, and reads it back with the product's own
/// reader: the field of exactly 262 144 decoded UTF-8 bytes appears once, contiguous, compared
/// with the source value character by character - on the consumer's own copy, never on anything
/// the service reports about it. The downloadable file of the three-byte case is the demo the
/// acceptance keeps, with its page count, size and SHA-256 recorded beside the run.
/// </summary>
public sealed class RowAtTheBoundEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    /// <summary>The template identity this class publishes the sample package under.</summary>
    private const string Template = "pilot-simple-at-bound";

    /// <summary>The shipped sample the package is built from.</summary>
    private const string Sample = "pilot-simple";

    private const string Version = "1.0.0";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan ReadyDeadline = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan TerminalDeadline = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The sample package's column captions, as a continuation page repeats them.</summary>
    private static readonly string[] PilotHeadRow = ["#", "Description", "Quantity", "Unit price", "Amount"];

    private const double PointsPerMillimetre = PlacementConvention.PointsPerMillimetre;

    private const double DescriptionColumnStartMm = 49.2;

    private const double DescriptionColumnEndMm = 86.4;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-row-at-bound",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public RowAtTheBoundEndToEndTests() => Directory.CreateDirectory(_directory);

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
    /// The three at-the-bound members with the characters their source value holds and the
    /// letters the reading compares: the reading uses the verification's own normalisation,
    /// which removes white space, so the matched run is the letters while the source's character
    /// count - letters and the 35 704 single spaces - is the number the acceptance names.
    /// </summary>
    public static TheoryData<string, int, int> AtTheBound => new()
    {
        { BoundFamily.Ascii, 262_144, 226_440 },
        { BoundFamily.TwoByte, 148_924, 113_220 },
        { BoundFamily.ThreeByte, 111_184, 75_480 },
    };

    [Theory]
    [MemberData(nameof(AtTheBound))]
    public async Task AConsumerDownloadsTheReportOfARecordAtTheBoundComplete(string fixture, int characters, int letters)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(cancellationToken);
        using var api = app.CreateHttpClient("api");
        var connectionString = await ConnectionStringAsync(app, cancellationToken);
        await TakenAsync(connectionString, cancellationToken);

        // The source value is the family's own string, exactly at the bound in decoded bytes.
        Assert.Equal(262_144, BoundFamily.Bytes(fixture));
        Assert.Equal(characters, BoundFamily.Characters(fixture));
        var snapshot = BoundFamily.Snapshot(fixture);

        var uploadId = await ReadyUploadAsync(api, snapshot.Bytes, cancellationToken);
        var reportJobId = await AcceptJobAsync(api, uploadId, cancellationToken);
        var (answer, body) = await WaitForTerminalAsync(api, reportJobId, cancellationToken);

        // The accepting side has nothing to fix: the job and its PDF output succeeded, the
        // output names its artifact, and neither the job nor the output carries an error.
        Assert.Equal("succeeded", answer.Status);
        Assert.Null(answer.ErrorCode);
        Assert.Null(answer.ErrorMessage);
        var output = Assert.Single(answer.Outputs);
        Assert.Equal("pdf", output.Format);
        Assert.Equal("succeeded", output.Status);
        Assert.Null(output.ErrorCode);
        Assert.Null(output.ErrorMessage);
        Assert.NotNull(output.ArtifactId);

        // One artifact, downloaded by the consumer and verified against the row the listing
        // answered: the bytes are the published ones, to the last hash.
        var listed = Assert.Single((await ListAsync(api, reportJobId, cancellationToken)).Artifacts);
        Assert.Equal("pdf", listed.Format);
        Assert.Equal("application/pdf", listed.MimeType);
        Assert.Equal(output.ArtifactId, listed.ArtifactId);

        byte[] downloaded;
        using (var response = await DownloadAsync(api, reportJobId, listed.ArtifactId, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(listed.SizeBytes, response.Content.Headers.ContentLength);
            downloaded = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        Assert.Equal(listed.SizeBytes, downloaded.LongLength);
        Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(downloaded)));

        // The consumer's own copy is read with the product's reader and compared with the source
        // value character by character: the whole field, contiguous, across more than one page,
        // every page carrying a word and no continuation mark.
        var path = WriteEvidence(fixture + ".downloaded.pdf", downloaded);
        try
        {
            var reading = PrintedFieldText.Read(
                path,
                15 * PointsPerMillimetre,
                12 * PointsPerMillimetre,
                18 * PointsPerMillimetre,
                12 * PointsPerMillimetre,
                PilotHeadRow,
                DescriptionColumnStartMm * PointsPerMillimetre,
                DescriptionColumnEndMm * PointsPerMillimetre);
            var match = reading.Find(BoundFamily.Description(fixture));

            Assert.True(match.Passed, $"{fixture}: {match.Difference} at {match.Position} (page {match.Page}).");
            Assert.Equal(letters, match.Length);
            Assert.True(match.LastPage > match.FirstPage, $"{fixture}: the row printed on one page.");
            Assert.True(reading.Pages.All(page => page.Words.Count > 0), $"{fixture}: a page printed no word.");
            Assert.DoesNotContain("(continues)", reading.Text, StringComparison.Ordinal);
            Assert.True(reading.Find("ordinary one").Passed, $"{fixture}: the record before the bound record is missing.");
            Assert.True(reading.Find("ordinary three").Passed, $"{fixture}: the record after the bound record is missing.");

            // The document was printed on the path without page accounting, so it carries no
            // running subtotal, brought-forward or carried-forward text of that path.
            AssertNoCarriedValue(reading);

            Record(fixture, snapshot, reportJobId, listed, reading.Pages.Count, match, letters, body, downloaded);
        }
        finally
        {
            if (RunEnvironment.RunDirectory is null)
            {
                File.Delete(path);
            }
        }
    }

    // The document, its identity and the reading stay with the run. The three-byte case also
    // leaves the file the acceptance keeps: the consumer's own download, unmodified.
    private static void Record(
        string fixture,
        SnapshotFixture snapshot,
        string reportJobId,
        ListedArtifact artifact,
        int pages,
        FieldMatch match,
        int letters,
        string body,
        byte[] downloaded)
    {
        if (RunEnvironment.RunDirectory is null)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"no {RunEnvironment.RunDirectoryVariable}: the evidence of this job was not written");
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"fixture={fixture}")
            .AppendLine($"sourceSha256={BoundFamily.SourceSha256(fixture)}")
            .AppendLine($"snapshotSha256={snapshot.Sha256}")
            .AppendLine($"reportJobId={reportJobId}")
            .AppendLine($"artifactId={artifact.ArtifactId}")
            .AppendLine($"pages={pages}")
            .AppendLine($"sizeBytes={artifact.SizeBytes}")
            .AppendLine($"sha256={artifact.Sha256}")
            .AppendLine($"matched={match.Length}")
            .AppendLine($"letters={letters}")
            .AppendLine($"firstPage={match.FirstPage}")
            .AppendLine($"lastPage={match.LastPage}")
            .ToString();
        RunEnvironment.WriteEvidence(fixture + ".end-to-end.txt", Encoding.UTF8.GetBytes(text));
        RunEnvironment.WriteEvidence(fixture + ".status.json", Encoding.UTF8.GetBytes(body));

        if (fixture != BoundFamily.ThreeByte)
        {
            return;
        }

        // The demo: the file a consumer downloaded, kept as it arrived, and the identity the
        // acceptance reads it by.
        var demoName = "row-at-the-bound-demo.pdf";
        var demoPath = RunEnvironment.WriteEvidence(demoName, downloaded);
        RunEnvironment.WriteEvidence(
            "row-at-the-bound-demo.txt",
            Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"{demoName} artifactId={artifact.ArtifactId} pages={pages} sizeBytes={downloaded.LongLength} sha256={RunEnvironment.Sha256Of(downloaded)}{Environment.NewLine}")));
        TestContext.Current.TestOutputHelper?.WriteLine($"demo: {demoPath} pages={pages} sha256={RunEnvironment.Sha256Of(downloaded)}");
    }

    private async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

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

    // The version the class's jobs are pinned to: the shipped sample published into the
    // composition's own storage root under this class's identity and taken here with the
    // product's own take, so every process of the composition reads the taken copy.
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

    // The sample template's own package with this class's identity and consumer: the document
    // prints the field as it is, and the sample already declares the named font family the
    // printing path is measured with, so nothing about its presentation changes.
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
                text = text.Replace("id: " + Sample, "id: " + Template, StringComparison.Ordinal);
                text = text.Replace("owner: dev-consumer", "owner: " + Consumer, StringComparison.Ordinal);
            }

            if (string.Equals(relative, "html/document-start.html.sbn", StringComparison.Ordinal))
            {
                text = text.Replace(
                    "<h1>{{ header.reportTitle | html.escape }}</h1>",
                    "<h1>{{ header.reportTitle | html.escape }}</h1>\n  <p class=\"template-version\">" + Template + " " + Version + "</p>",
                    StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    // A ready upload of the snapshot through the composition's own API: created, its content
    // sent, its completion accepted and its ready state read back.
    private static async Task<string> ReadyUploadAsync(HttpClient api, byte[] document, CancellationToken cancellationToken)
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

    private static async Task<(StatusAnswer Answer, string Body)> WaitForTerminalAsync(
        HttpClient api,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        (StatusAnswer Answer, string Body)? last = null;
        await Wait.UntilAsync(
            $"the job {reportJobId} to end",
            async token =>
            {
                last = await ReadStatusAsync(api, reportJobId, token);
                return last.Value.Answer.Status is "failed" or "succeeded" or "cancelled";
            },
            TerminalDeadline,
            PollInterval,
            cancellationToken);
        return last!.Value;
    }

    private static async Task<(StatusAnswer Answer, string Body)> ReadStatusAsync(
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
            new StatusAnswer(
                root.GetProperty("status").GetString()!,
                Text(root, "errorCode"),
                Text(root, "errorMessage"),
                [
                    .. root.GetProperty("outputs").EnumerateArray().Select(element => new OutputAnswer(
                        element.GetProperty("format").GetString()!,
                        element.GetProperty("status").GetString()!,
                        Text(element, "errorCode"),
                        Text(element, "errorMessage"),
                        Text(element, "artifactId"))),
                ]),
            body);
    }

    private static string? Text(JsonElement element, string name) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.Null } ? null : element.GetProperty(name).GetString();

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
                artifact.GetProperty("sha256").GetString()!));
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

    // The path prints no page accounting: no running subtotal and no carried value appears in
    // the document's own words.
    private static void AssertNoCarriedValue(PrintedFieldReading reading)
    {
        var printed = string.Join(" ", reading.Pages.SelectMany(page => page.Words).Select(word => word.Text));
        Assert.DoesNotContain("subtotal", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brought forward", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("carried forward", printed, StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteEvidence(string fileName, byte[] content)
    {
        if (RunEnvironment.RunDirectory is not null)
        {
            return RunEnvironment.WriteEvidence(fileName, content);
        }

        var path = Path.Combine(Path.GetTempPath(), "report-service-e2e-bound-" + Guid.NewGuid().ToString("N") + "-" + fileName);
        File.WriteAllBytes(path, content);
        return path;
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
        string Sha256);

    /// <summary>One answer of the job status read.</summary>
    private sealed record StatusAnswer(
        string Status,
        string? ErrorCode,
        string? ErrorMessage,
        IReadOnlyList<OutputAnswer> Outputs);

    /// <summary>One requested output of a job's answer.</summary>
    private sealed record OutputAnswer(
        string Format,
        string Status,
        string? ErrorCode,
        string? ErrorMessage,
        string? ArtifactId);
}
