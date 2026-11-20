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
using ReportService.TemplateSource.Manifest;
using ReportService.TemplateSource.Source;
using ReportService.TemplateSource.Take;
using ReportService.Testing;
using ReportService.Testing.TemplateSource;
using ReportService.Uploads;
using UglyToad.PdfPig.Content;

namespace ReportService.EndToEndTests;

/// <summary>
/// The presentation over the whole local composition: a job accepted with a culture and a time
/// zone is printed by the running service, ends <c>succeeded</c>, publishes one file whose
/// object verifies by size and SHA-256, and the published document carries that culture's dates
/// and numbers and the job's own acceptance instant as its "now".
/// <para>
/// The document is read out of the published file itself and compared with expectations the
/// case computes from the snapshot and the job's records - never with anything the composition
/// reported about it. The version is taken before the composition serves the job, because the
/// render worker is a container and the template source is outside it.
/// </para>
/// </summary>
[Trait("Requires", "printing")]
public sealed class EnginePresentationEndToEndTests : IDisposable
{
    private const string Consumer = "consumer-a";

    private const string Template = "same-report-print";

    private const string Sample = "same-report";

    private const string Version = "1.0.0";

    private const string Culture = "es-AR";

    private const string TimeZone = "America/Argentina/Buenos_Aires";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly TimeSpan PublishedDeadline = TimeSpan.FromMinutes(5);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "report-service-e2e-presentation",
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]);

    /// <summary>Creates the directory the temporary registry lives in.</summary>
    public EnginePresentationEndToEndTests() => Directory.CreateDirectory(_directory);

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
    public async Task AJobAcceptedWithACultureAndAZonePrintsItsOwnPresentation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var composition = await StartAsync(cancellationToken);
        var connectionString = await composition.GetConnectionStringAsync("reportservice", cancellationToken)
            ?? throw new InvalidOperationException("the composition had no connection string for its metadata database");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var storage = FilesystemObjectStore.Open(
            new FilesystemStorageOptions { Root = StorageRoot(), CreateIfMissing = true },
            StorageRoot(),
            TimeProvider.System,
            new NullLogger<FilesystemObjectStore>());
        var uploads = new PostgresUploadStore(dataSource);
        var versions = new PostgresTemplateVersionStore(dataSource);
        using var api = composition.CreateHttpClient("api");

        var snapshot = Snapshot(25);
        var amount = AmountOf(snapshot);
        var uploadId = await ReadyUploadAsync(uploads, storage, snapshot, cancellationToken);
        await TakenAsync(dataSource, versions, storage, cancellationToken);

        var acceptedAt = DateTimeOffset.UtcNow;
        var reportJobId = await SubmitAsync(api, uploadId, cancellationToken);
        var job = await WaitForSucceededAsync(dataSource, reportJobId, cancellationToken);

        // One artifact, whose object verifies by size and SHA-256 through the storage port.
        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Matches("^artifact-[0-9a-f]{32}$", job.ArtifactId);
        var read = await storage.OpenReadAsync(
            ObjectKey.Create(job.ObjectKey!),
            job.SizeBytes!.Value,
            job.Sha256!,
            cancellationToken);
        Assert.True(read.Succeeded, $"the artifact of {reportJobId} had to verify through the storage port");
        using var buffer = new MemoryStream();
        await using (var content = read.Value!)
        {
            await content.Content.CopyToAsync(buffer, cancellationToken);
        }

        var bytes = buffer.ToArray();
        Assert.Equal(job.Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));

        // The document itself: the job's culture's number text and the acceptance instant, both
        // computed here from the snapshot and the job's record.
        var text = TextOf(bytes);
        Assert.Contains(amount.ToString("N2", CultureInfo.GetCultureInfo(Culture)), text, StringComparison.Ordinal);
        var clock = await ReportClockAsync(dataSource, reportJobId, cancellationToken);
        Assert.InRange(clock, acceptedAt.AddMinutes(-1), acceptedAt.AddMinutes(1));
        var expectedInstant = TimeZoneInfo.ConvertTime(clock, TimeZoneInfo.FindSystemTimeZoneById(TimeZone))
            .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains(expectedInstant, text, StringComparison.Ordinal);

        var path = RunEnvironment.WriteEvidence("same-report-presentation-demo.pdf", bytes);
        var hash = RunEnvironment.Sha256Of(bytes);
        RunEnvironment.WriteEvidence(
            "same-report-presentation-demo.evidence.txt",
            Encoding.UTF8.GetBytes(
                $"same-report-presentation-demo.pdf sha256={hash} bytes={bytes.Length} culture={Culture} zone={TimeZone} now={expectedInstant}{Environment.NewLine}"));
        TestContext.Current.TestOutputHelper?.WriteLine($"demo: {path} sha256={hash} now={expectedInstant}");
    }

    private static async Task<DistributedApplication> StartAsync(CancellationToken cancellationToken)
    {
        var registry = Path.Combine(Path.GetTempPath(), "report-service-e2e-presentation", "consumers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(
            registry,
            """{ "consumers": [ { "consumerId": "consumer-a", "displayName": "Consumer A", "active": true } ] }""",
            cancellationToken);

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

    // A ready upload of the snapshot, written through the composition's own records and storage.
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

    // The version the job prints, taken here with the product's own take: the version's bytes go
    // to the composition's shared storage root and its record to the metadata database, so every
    // process of the composition sees the taken copy.
    private static async Task TakenAsync(
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

        var outcome = await take.TakeAsync(Template, Version, Consumer, reportJobId: null, cancellationToken);
        Assert.NotNull(outcome.Record);
        Assert.Equal(Culture, outcome.Record!.Manifest.LocaleDefaults?.Culture);
        Assert.Equal(TimeZone, outcome.Record!.Manifest.LocaleDefaults?.TimeZone);
    }

    // The one report's own package, with the identity this class publishes under: the id and
    // the owner are the only bytes that differ from the shipped package.
    private static Dictionary<string, byte[]> PackageFiles()
    {
        var root = Path.Combine(RepositoryPaths.Root, "tests", "ReportService.Testing", "TemplatePackages", Sample, Version);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(file);
            if (string.Equals(relative, "template.yaml", StringComparison.Ordinal))
            {
                text = text.Replace("id: " + Sample, "id: " + Template, StringComparison.Ordinal);
                text = text.Replace(
                    "localeDefaults:",
                    "owner: " + Consumer + Environment.NewLine + "localeDefaults:",
                    StringComparison.Ordinal);
            }

            files[relative] = Encoding.UTF8.GetBytes(text);
        }

        return files;
    }

    private static byte[] Snapshot(int records)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryPaths.Root,
            "samples",
            "templates",
            "pilot-simple",
            "sample.json")))!;
        var items = sample["items"]!.AsArray();
        var built = new JsonArray();
        for (var index = 0; index < records; index++)
        {
            var item = JsonNode.Parse(items[index % items.Count]!.ToJsonString())!;
            item["sequence"] = index + 1;

            // The sample's contract has no date field, but this package's 1.0.0 schema requires
            // one on every record and the row prints it. One instant for every record, at 02:30
            // UTC: the day it prints is the day the job's own zone says it is, mirroring the
            // integration case's Snapshot.
            item["date"] = "2026-03-14T02:30:00Z";
            built.Add(item);
        }

        sample["items"] = built;
        sample["header"]!["totals"]!["recordCount"] = records;
        return Encoding.UTF8.GetBytes(sample.ToJsonString());
    }

    private static decimal AmountOf(byte[] snapshot)
    {
        using var document = JsonDocument.Parse(snapshot);
        var item = document.RootElement.GetProperty("items").EnumerateArray().First();
        return decimal.Parse(item.GetProperty("amount").GetRawText(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> SubmitAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(CreateReportJobEndpoint.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(
                "{\"template\":{\"id\":\"" + Template + "\",\"version\":\"" + Version + "\"},"
                + "\"data\":{\"uploadId\":\"" + uploadId + "\",\"profile\":\"header-items-v1\"},"
                + "\"outputs\":[\"pdf\"],\"render\":{\"culture\":\"" + Culture + "\",\"timeZone\":\"" + TimeZone + "\"}}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, Consumer);
        using var response = await api.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the job had to be accepted: {(int)response.StatusCode} {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("reportJobId").GetString()!;
    }

    private static async Task<DateTimeOffset> ReportClockAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT report_clock FROM report_service.report_jobs WHERE report_job_id = @id";
        command.Parameters.Add(new NpgsqlParameter("id", reportJobId));

        // Npgsql reads a timestamptz as a UTC DateTime, as the job store does for this column;
        // the instant is lifted to DateTimeOffset by stating the zone the database already returns.
        var value = (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private static async Task<PublishedJob> WaitForSucceededAsync(
        NpgsqlDataSource dataSource,
        string reportJobId,
        CancellationToken cancellationToken)
    {
        PublishedJob? job = null;
        await Wait.UntilAsync(
            $"job {reportJobId} to be published",
            async token =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT j.status, a.artifact_id, a.object_key, a.sha256, a.size_bytes
                      FROM report_service.report_jobs j
                      LEFT JOIN report_service.report_outputs o ON o.report_job_id = j.report_job_id
                      LEFT JOIN report_service.artifacts a ON a.output_id = o.output_id
                     WHERE j.report_job_id = @id
                    """;
                command.Parameters.Add(new NpgsqlParameter("id", reportJobId));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    job = new PublishedJob(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4));
                }

                return job is { Status: "SUCCEEDED" or "FAILED" or "CANCELLED" };
            },
            PublishedDeadline,
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        return job!;
    }

    // The text of a printed document, page by page, as the page presents it.
    private static string TextOf(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), "report-service-e2e-presentation-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var wordStarts = new HashSet<Letter>(ReferenceEqualityComparer.Instance);
                foreach (var word in page.GetWords())
                {
                    if (word.Letters.Count > 0)
                    {
                        wordStarts.Add(word.Letters[0]);
                    }
                }

                var pageText = new StringBuilder();
                foreach (var letter in page.Letters)
                {
                    if (wordStarts.Contains(letter) && pageText.Length > 0 && !char.IsWhiteSpace(pageText[^1]))
                    {
                        pageText.Append(' ');
                    }

                    pageText.Append(letter.Value);
                }

                text.Append(pageText).Append(' ');
            }

            return text.ToString();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string StorageRoot() => Path.Combine(RepositoryPaths.Root, ".storage");

    private sealed record PublishedJob(string Status, string? ArtifactId, string? ObjectKey, string? Sha256, long? SizeBytes);
}
