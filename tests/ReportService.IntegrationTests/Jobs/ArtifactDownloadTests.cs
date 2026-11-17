using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Telemetry;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Storage;
using ReportService.Testing;

namespace ReportService.IntegrationTests.Jobs;

/// <summary>
/// The list and the download against the composition's own processes: a real PostgreSQL, a real
/// Temporal engine and the product's API, orchestration worker and render worker hosted from their
/// real registrations, with the API's clock in the case's hand. A job held at the step that renders
/// its PDF lists nothing; once it succeeded the list holds exactly the artifact the database holds,
/// with the expiry the publication recorded; the downloaded bytes are the stored object's own, and
/// the hash and the size the case computes are the list's and the row's. A stored object whose bytes
/// no longer match ends as an incomplete transfer, and once the recorded expiry has passed the
/// download answers that the artifact expired while the job is still succeeded and nothing was
/// written.
/// </summary>
[Trait("Requires", "printing")]
public sealed class ArtifactDownloadTests : IClassFixture<PostgresFixture>
{
    private const string Consumer = PrintingRig.Consumer;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    private readonly PostgresFixture _fixture;

    private readonly DownloadInstruments _instruments = new();

    /// <summary>Runs against the class's PostgreSQL server.</summary>
    /// <param name="fixture">The container this class owns.</param>
    public ArtifactDownloadTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheListIsEmptyWhileThePdfIsNotPublishedAndHoldsTheRowsArtifactOnceItIs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, startPdf: false, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);

        // The job is held at the step that renders its PDF: nothing is published, so the list is
        // empty — several times, because a listing that had to guess would be wrong only later.
        for (var read = 0; read < 3; read++)
        {
            Assert.Empty((await ListAsync(client, reportJobId, cancellationToken)).Artifacts);
        }

        await rig.StartPdfAsync(cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
        Assert.Equal("SUCCEEDED", job.Status);

        var answer = await ListAsync(client, reportJobId, cancellationToken);
        Assert.Equal(reportJobId, answer.ReportJobId);
        Assert.True(answer.NoStore, "a listing is never cached");
        var artifact = Assert.Single(answer.Artifacts);
        Assert.Equal(job.ArtifactId, artifact.ArtifactId);
        Assert.Equal("pdf", artifact.Format);
        Assert.Equal("application/pdf", artifact.MimeType);
        Assert.Equal(job.SizeBytes, artifact.SizeBytes);
        Assert.Equal(job.Sha256, artifact.Sha256);

        // The row the publication wrote: the expiry is the artifact's own creation plus the
        // configured hour, and the answer carries that instant, not one computed at read time.
        Assert.Equal(TimeSpan.FromHours(1), job.ExpiresAt!.Value - job.ArtifactCreatedAt!.Value);
        Assert.Equal(JobWireValues.TruncateToMillisecond(job.ExpiresAt.Value), artifact.ExpiresAt);
    }

    [Fact]
    public async Task TheDownloadedBytesAreTheListedHashAndSizeAndTheRowAndTheStoredObject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
        Assert.Equal("SUCCEEDED", job.Status);

        var listed = Assert.Single((await ListAsync(client, reportJobId, cancellationToken)).Artifacts);
        using var response = await DownloadAsync(client, reportJobId, listed.ArtifactId, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(listed.SizeBytes, response.Content.Headers.ContentLength);

        var installed = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // The case computes the figures itself and compares them with every place the composition
        // carries them: the list, the row and the object as the storage port reads it back.
        Assert.Equal(listed.SizeBytes, installed.LongLength);
        Assert.Equal(listed.SizeBytes, job.SizeBytes);
        Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(installed)));
        Assert.Equal(listed.Sha256, job.Sha256);
        Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(
            await rig.ReadArtifactAsync(job, cancellationToken))));
        Assert.Equal(await rig.ReadArtifactAsync(job, cancellationToken), installed);
    }

    [Fact]
    public async Task AnEmptySnapshotJobListsAndDownloadsItsOneFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.EmptySnapshot(), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);

        Assert.Equal("SUCCEEDED", job.Status);
        Assert.Equal(0, job.RecordCount);
        var listed = Assert.Single((await ListAsync(client, reportJobId, cancellationToken)).Artifacts);
        using var response = await DownloadAsync(client, reportJobId, listed.ArtifactId, cancellationToken);
        var installed = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(listed.SizeBytes, installed.LongLength);
        Assert.Equal(listed.Sha256, Convert.ToHexStringLower(SHA256.HashData(installed)));
        Assert.Equal(job.ArtifactId, listed.ArtifactId);
    }

    [Fact]
    public async Task AnExpiredArtifactAnswersGoneWhileTheAbsentOneAnswersNotFoundAndTheJobIsUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var rig = await PrintingRig.StartAsync(_fixture, clock: clock, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
        Assert.Equal("SUCCEEDED", job.Status);

        var listed = Assert.Single((await ListAsync(client, reportJobId, cancellationToken)).Artifacts);
        using var before = await DownloadAsync(client, reportJobId, listed.ArtifactId, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // The API's clock reaches the recorded instant exactly: the artifact is no longer
        // downloadable, the absent identifier is not found, and the two answers differ.
        clock.Advance(job.ExpiresAt!.Value - clock.GetUtcNow());
        using var expired = await DownloadAsync(client, reportJobId, listed.ArtifactId, cancellationToken);
        using var absent = await DownloadAsync(
            client, reportJobId, "artifact-ffffffffffffffffffffffffffffffff", cancellationToken);
        var expiredAnswer = await JobAnswerAsync(expired);
        var absentAnswer = await JobAnswerAsync(absent);

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal(ErrorCodes.ArtifactExpired, expiredAnswer.GetProperty("code").GetString());
        Assert.Equal(ArtifactDownloadEndpoint.ExpiredTitle, expiredAnswer.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(ErrorCodes.InputNotFound, absentAnswer.GetProperty("code").GetString());

        // Afterwards the job reads succeeded with the same artifact, and every row is the value it
        // carried before the attempt.
        var after = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
        Assert.Equal(job.Status, after.Status);
        Assert.Equal(job.OutputStatus, after.OutputStatus);
        Assert.Equal(job.ErrorCode, after.ErrorCode);
        Assert.Equal(job.ArtifactId, after.ArtifactId);
        Assert.Equal(job.Sha256, after.Sha256);
        Assert.Equal(job.SizeBytes, after.SizeBytes);
        Assert.Equal(job.ExpiresAt, after.ExpiresAt);

        // Nothing was deleted: an expired artifact keeps its bytes, and the answer never needed them.
        Assert.True(File.Exists(PathOf(rig, job.ObjectKey!)), "an expired artifact's object is not deleted by the read");
    }

    [Fact]
    public async Task ACorruptStoredObjectEndsTheTransferInsteadOfDeliveringIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();
        var reportJobId = await rig.SubmitAsync(
            await rig.ReadyUploadAsync(PrintingRig.Snapshot(25), cancellationToken),
            PrintingRig.First,
            cancellationToken);
        var job = await rig.WaitForTerminalAsync(reportJobId, Deadline, cancellationToken);
        Assert.Equal("SUCCEEDED", job.Status);

        // The stored object is altered after it was published: one byte of it changed, its length
        // untouched, so only the end-of-stream comparison can notice.
        var path = PathOf(rig, job.ObjectKey!);
        var stored = await File.ReadAllBytesAsync(path, cancellationToken);
        stored[stored.Length / 2] ^= 0x5a;
        await File.WriteAllBytesAsync(path, stored, cancellationToken);
        _instruments.Reset();

        // The transfer is cut as soon as the reader refuses. A stored object larger than one chunk
        // starts the response and then fails on the read; one of a single chunk is cut before any
        // byte reached the client, so the send itself fails. Both shapes are the same answer: fewer
        // bytes than the length the row declares.
        var received = 0L;
        Exception? failure;
        HttpResponseMessage? response = null;
        try
        {
            response = await DownloadAsync(client, reportJobId, job.ArtifactId!, cancellationToken);
            (received, failure) = await ReadBodyAsync(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(job.SizeBytes, response.Content.Headers.ContentLength);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
        {
            failure = exception;
        }

        using (response)
        {
            Assert.NotNull(failure);
            Assert.True(
                received < job.SizeBytes!.Value,
                $"a corrupt object has to arrive incomplete: {received} of {job.SizeBytes} bytes arrived");
        }

        // The transfer was cut and counted, and the record names the artifact and never the key.
        Assert.Equal(1, _instruments.IntegrityFailures);
        Assert.Contains(
            rig.Logs.Lines,
            line => line.StartsWith("Error:", StringComparison.Ordinal)
                && line.Contains("aborted", StringComparison.Ordinal)
                && line.Contains(job.ArtifactId!, StringComparison.Ordinal)
                && !line.Contains(job.ObjectKey!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInactiveConsumerListsAndDownloadsTheJobItAlreadyOwns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var rig = await PrintingRig.StartAsync(_fixture, cancellationToken: cancellationToken);
        using var client = rig.CreateApiClient();

        // A job of the consumer the registry carries as inactive, recorded through the composition's
        // own store: an inactive consumer may not start a new job, and everything it already owns
        // stays readable.
        var uploadId = await rig.ReadyUploadAsync(
            PrintingRig.Snapshot(25), cancellationToken, PrintingRig.InactiveConsumer);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            JobIdentifier.CreateOutput(),
            PrintingRig.InactiveConsumer,
            uploadId,
            PrintingRig.Template,
            TemplateReferenceForm.Exact,
            PrintingRig.First);
        Assert.IsType<JobAcceptanceResult.Accepted>(
            await rig.Jobs.AcceptAsync(acceptance, DateTimeOffset.UtcNow, cancellationToken));
        Assert.Equal(
            TransitionOutcome.Applied,
            await rig.Jobs.TransitionOutputAsync(acceptance.OutputId, DateTimeOffset.UtcNow, cancellationToken));

        var content = "the inactive consumer's own file"u8.ToArray();
        var key = ObjectKey.FromSegments("outputs", acceptance.ReportJobId, acceptance.OutputId, "attempt");
        using (var source = new MemoryStream(content, writable: false))
        {
            var written = await rig.Objects.WriteAsync(key, source, cancellationToken);
            Assert.True(written.Succeeded, $"the case's file had to be stored: {written.Outcome}");
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(
                PublishOutcome.Published,
                (await rig.Jobs.PublishAsync(
                    new ArtifactRecord(
                        ArtifactIdentifier.Create(),
                        acceptance.ReportJobId,
                        acceptance.OutputId,
                        OutputFormat.Pdf,
                        "application/pdf",
                        key.ToString(),
                        written.Value!.Sha256,
                        written.Value.Size,
                        1,
                        now,
                        now.AddHours(1)),
                    cancellationToken)).Outcome);
        }

        Assert.Equal(JobFinalizationOutcome.Applied, (await rig.Jobs.FinalizeJobAsync(
            acceptance.ReportJobId, DateTimeOffset.UtcNow, cancellationToken)).Outcome);

        var listed = Assert.Single(
            (await ListAsync(client, acceptance.ReportJobId, cancellationToken, PrintingRig.InactiveConsumer)).Artifacts);
        using var response = await DownloadAsync(
            client, acceptance.ReportJobId, listed.ArtifactId, cancellationToken, PrintingRig.InactiveConsumer);
        var installed = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, installed);
    }

    private static async Task<Listing> ListAsync(
        HttpClient client,
        string reportJobId,
        CancellationToken cancellationToken,
        string consumer = Consumer)
    {
        using var response = await client.SendAsync(ListRequest(reportJobId, consumer), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var artifacts = new List<ListedArtifact>();
        foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
        {
            artifacts.Add(new ListedArtifact(
                artifact.GetProperty("artifactId").GetString()!,
                artifact.GetProperty("format").GetString()!,
                artifact.GetProperty("mimeType").GetString()!,
                artifact.GetProperty("sizeBytes").GetInt64(),
                artifact.GetProperty("sha256").GetString()!,
                Instant(artifact.GetProperty("expiresAt").GetString()!)));
        }

        return new Listing(
            root.GetProperty("reportJobId").GetString()!,
            artifacts,
            string.Equals(response.Headers.CacheControl?.ToString(), "no-store", StringComparison.Ordinal));
    }

    private static Task<HttpResponseMessage> DownloadAsync(
        HttpClient client,
        string reportJobId,
        string artifactId,
        CancellationToken cancellationToken,
        string consumer = Consumer) =>
        client.SendAsync(
            DownloadRequest(reportJobId, artifactId, consumer),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

    // Reads the body the way a client does: the declared length is known before the first byte, and
    // a transfer the service cut raises on the read that finds the connection gone.
    private static async Task<(long Received, Exception? Failure)> ReadBodyAsync(HttpResponseMessage response)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8 * 1024];
        var received = 0L;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return (received, null);
                }

                received += read;
            }
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException)
        {
            return (received, exception);
        }
    }

    private static HttpRequestMessage ListRequest(string reportJobId, string consumer)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        return message;
    }

    private static HttpRequestMessage DownloadRequest(string reportJobId, string artifactId, string consumer)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{reportJobId}{ArtifactListEndpoint.RouteSuffix}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumer);
        return message;
    }

    private static async Task<JsonElement> JobAnswerAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static string PathOf(PrintingRig rig, string objectKey) =>
        Path.Combine([rig.StorageRoot, .. ObjectKey.Create(objectKey).Segments]);

    private static DateTimeOffset Instant(string text) =>
        DateTimeOffset.ParseExact(
            text,
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>One answer of the listing.</summary>
    private sealed record Listing(string ReportJobId, IReadOnlyList<ListedArtifact> Artifacts, bool NoStore);

    /// <summary>One artifact the listing carries.</summary>
    private sealed record ListedArtifact(
        string ArtifactId,
        string Format,
        string MimeType,
        long SizeBytes,
        string Sha256,
        DateTimeOffset ExpiresAt);

    /// <summary>
    /// Counts the transfers the API's own instrument reports as cut, so a case can say that a corrupt
    /// object was cut and counted, not merely refused.
    /// </summary>
    private sealed class DownloadInstruments : IDisposable
    {
        private readonly MeterListener _listener;
        private long _integrityFailures;

        public DownloadInstruments()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (string.Equals(instrument.Meter.Name, TelemetryDefaults.MeterName, StringComparison.Ordinal)
                        && string.Equals(instrument.Name, ArtifactDownloadMetrics.IntegrityFailuresInstrumentName, StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, _, _) =>
                {
                    if (string.Equals(instrument.Name, ArtifactDownloadMetrics.IntegrityFailuresInstrumentName, StringComparison.Ordinal))
                    {
                        Interlocked.Add(ref _integrityFailures, measurement);
                    }
                });
            _listener.Start();
        }

        /// <summary>How many transfers were counted as cut since the count was last reset.</summary>
        public long IntegrityFailures => Interlocked.Read(ref _integrityFailures);

        /// <summary>Sets the count back to zero.</summary>
        public void Reset() => Interlocked.Exchange(ref _integrityFailures, 0);

        public void Dispose() => _listener.Dispose();
    }
}
