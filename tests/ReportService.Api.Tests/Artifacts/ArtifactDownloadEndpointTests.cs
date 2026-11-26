using System.Globalization;
using System.Net;
using ReportService.Api.Artifacts;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Jobs;
using ReportService.Api.Tests.Jobs;
using ReportService.Jobs;
using ReportService.Storage;
using ReportService.Testing;

namespace ReportService.Api.Tests.Artifacts;

/// <summary>
/// Downloading one published artifact through the API's real pipeline: the declared status and
/// headers and a body whose bytes hash to the listed SHA-256 and have the listed size, the empty
/// snapshot's one file, the expiry boundary to the millisecond and the expired answer that reads
/// metadata alone, a stored object that no longer matches its size or its hash and ends as an
/// incomplete transfer, and the refusals — one not-found answer for every unusable reference, a
/// missing identity that reaches no store, an inactive consumer that still downloads, and a
/// storage the service cannot read. The objects are written through the storage port and altered
/// on disk afterwards, so their size and hash are the store's own and a corruption is exactly the
/// corruption a case describes.
/// </summary>
public sealed class ArtifactDownloadEndpointTests : IDisposable
{
    private const string AnotherConsumer = "another-consumer";

    private static readonly DateTimeOffset Now = JobWireValues.TruncateToMillisecond(JobApiHost.Now);

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>The meter this case's hosts publish on, so its listener hears no neighbour's download.</summary>
    private readonly string _meterName = "reportservice-artifact-download-" + Guid.NewGuid().ToString("N");

    private readonly DownloadInstruments _instruments;

    private JobApiHost _host;

    /// <summary>Starts the listener and the host on a meter of this case's own.</summary>
    public ArtifactDownloadEndpointTests()
    {
        _instruments = new DownloadInstruments(_meterName);
        _host = new JobApiHost(meterName: _meterName);
    }

    public void Dispose()
    {
        _host.Dispose();
        _instruments.Dispose();
    }

    [Fact]
    public async Task APublishedArtifactIsDownloadedWithItsDeclaredHeadersAndItsWholeContent()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, content) = await PublishedAsync(jobId, outputId, Body(40_000));

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var (received, failure) = await ReadBodyAsync(response);

        Assert.Null(failure);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(artifact.SizeBytes, response.Content.Headers.ContentLength);
        Assert.Equal(artifact.SizeBytes, received);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Null(response.Headers.ETag);
        Assert.Empty(response.Headers.AcceptRanges);

        // The name is built from the job's own identifier and the artifact's format, never from
        // anything a caller sent.
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal($"{jobId}.pdf", disposition.FileName?.Trim('"'));
        Assert.Equal(artifact.Sha256, HashOf(content));
    }

    // US-008 AC-8 (ADR-047 HTML-output contract): a succeeded HTML output's artifact downloads with
    // Content-Type text/html; charset=utf-8 and filename {jobId}.html — the same serve path the PDF
    // takes, with the format's own media type and extension. The filename is built from the job's
    // identifier and the artifact's format, never from anything a caller sent. Oracle: AC-8 + ADR-047.
    [Fact]
    public async Task AnHtmlArtifactIsDownloadedAsTextHtmlWithTheJobFilename()
    {
        var (jobId, artifact, content) = await PublishedHtmlAsync(Body(12_000));

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var (received, failure) = await ReadBodyAsync(response);

        Assert.Null(failure);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(artifact.SizeBytes, response.Content.Headers.ContentLength);
        Assert.Equal(artifact.SizeBytes, received);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Equal($"{jobId}.html", disposition.FileName?.Trim('"'));
        Assert.Equal(artifact.Sha256, HashOf(content));
    }

    [Fact]
    public async Task TheBodyIsTheStoredObjectAndItsHashIsTheOneTheListingCarries()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(70_000));

        using var listing = await _host.Client.SendAsync(ListRequest(JobApiHost.Consumer, jobId), TestContext.Current.CancellationToken);
        using var download = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);

        var listed = Assert.Single((await JobApiHost.AnswerAsync(listing)).GetProperty("artifacts").EnumerateArray());
        var installed = await download.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(installed)), listed.GetProperty("sha256").GetString());
        Assert.Equal(installed.LongLength, listed.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(artifact.SizeBytes, listed.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task TheDownloadOfAZeroRecordSnapshotJobDeliversItsOneFile()
    {
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.CommitPartitionAsync(Partition(jobId, recordCount: 0), TestContext.Current.CancellationToken);
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(4_096), pageCount: 1);

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var installed = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(artifact.SizeBytes, installed.LongLength);
        Assert.Equal(artifact.Sha256, HashOf(installed));
    }

    [Fact]
    public async Task AnArtifactOneMillisecondBeforeItsExpiryIsStillDownloaded()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, content) = await PublishedAsync(jobId, outputId, Body(2_048));

        _host.Clock.Advance(Retention - TimeSpan.FromMilliseconds(1));

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var installed = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, installed);
    }

    [Fact]
    public async Task AnArtifactAtItsExpiryAndAfterIsAnsweredExpiredWithoutReadingTheStorage()
    {
        var observed = ObserveStorage();
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(2_048));
        Assert.Equal(Retention, artifact.ExpiresAt - artifact.CreatedAt);

        // At the recorded instant exactly the artifact is no longer downloadable: the boundary
        // the API keeps for every expiry.
        _host.Clock.Advance(Retention);
        var writesBefore = _host.Jobs.WriteCalls;
        using var atExpiry = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var answer = await JobApiHost.AnswerAsync(atExpiry);

        Assert.Equal(HttpStatusCode.Gone, atExpiry.StatusCode);
        Assert.Equal("application/problem+json", atExpiry.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ErrorCodes.ArtifactExpired, answer.GetProperty("code").GetString());
        Assert.Equal(ArtifactDownloadEndpoint.ExpiredTitle, answer.GetProperty("title").GetString());
        Assert.Equal(ArtifactDownloadEndpoint.ExpiredDetail, answer.GetProperty("detail").GetString());

        _host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        using var afterExpiry = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);

        Assert.Equal(HttpStatusCode.Gone, afterExpiry.StatusCode);

        // The expiry is decided from the row: the object store was never opened, and nothing was
        // written — not the job, not its output, not the artifact.
        Assert.Equal(0, observed.Opens);
        Assert.Equal(writesBefore, _host.Jobs.WriteCalls);
        var view = await _host.Jobs.FindStatusAsync(jobId, JobApiHost.Consumer, TestContext.Current.CancellationToken);
        Assert.Equal(ReportJobStatus.Succeeded, view!.Status);
        Assert.Equal(artifact.ArtifactId, Assert.Single(view.Outputs).ArtifactId);
        Assert.Equal(artifact, _host.Jobs.ArtifactOf(outputId));
        Assert.True(File.Exists(PathOf(artifact.ObjectKey)), "the stored object is not deleted by an expired answer");
    }

    [Fact]
    public async Task AnExpiredArtifactWhoseObjectIsGoneStillAnswersExpired()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(1_024));
        File.Delete(PathOf(artifact.ObjectKey));

        _host.Clock.Advance(Retention);

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);

        // The expiry is not decided by whether the bytes are still there.
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ArtifactExpired,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheExpiredAnswerDiffersFromTheNotFoundAnswerAndFromASuccessfulDownload()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(1_024));

        using var downloaded = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        using var missing = await DownloadAsync(
            JobApiHost.Consumer, jobId, "artifact-ffffffffffffffffffffffffffffffff");
        _host.Clock.Advance(Retention);
        using var expired = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);

        var expiredAnswer = await JobApiHost.AnswerAsync(expired);
        var missingAnswer = await JobApiHost.AnswerAsync(missing);

        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);

        Assert.Equal(ErrorCodes.InputNotFound, missingAnswer.GetProperty("code").GetString());
        Assert.Equal(ArtifactDownloadEndpoint.NotFoundTitle, missingAnswer.GetProperty("title").GetString());
        Assert.NotEqual(missingAnswer.GetProperty("code").GetString(), expiredAnswer.GetProperty("code").GetString());
        Assert.NotEqual(missingAnswer.GetProperty("title").GetString(), expiredAnswer.GetProperty("title").GetString());
        Assert.NotEqual(
            downloaded.Content.Headers.ContentType?.MediaType,
            expired.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("flipped")]
    [InlineData("missing")]
    [InlineData("extra")]
    public async Task ACorruptStoredObjectEndsTheTransferInsteadOfDeliveringIt(string corruption)
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(3 * 64 * 1024));
        Corrupt(PathOf(artifact.ObjectKey), corruption);
        _instruments.Reset();

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var (received, failure) = await ReadBodyAsync(response);

        // The answer starts as a download and announces the whole length; the bytes that arrive
        // are fewer, and the client's own read fails.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(artifact.SizeBytes, response.Content.Headers.ContentLength);
        Assert.NotNull(failure);
        Assert.True(
            received < artifact.SizeBytes,
            $"a corrupt object has to arrive incomplete: {received} of {artifact.SizeBytes} bytes arrived");
        Assert.Equal(1, _instruments.IntegrityFailures);

        var error = Assert.Single(
            _host.Logs.Lines,
            line => line.StartsWith("Error:", StringComparison.Ordinal)
                && line.Contains("aborted", StringComparison.Ordinal));
        Assert.Contains(artifact.ArtifactId, error, StringComparison.Ordinal);
        Assert.DoesNotContain(artifact.ObjectKey, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForeignAnAbsentOrAMalformedReferenceAnswersOneNotFound()
    {
        var (ownJob, ownOutput) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(ownJob, ownOutput, Body(512));
        var (foreignJob, foreignOutput) = await AcceptedAsync(AnotherConsumer);
        var (foreignArtifact, _) = await PublishedAsync(foreignJob, foreignOutput, Body(512));

        // An artifact of another job of the same consumer: the artifact exists, but not under
        // the job the request names.
        var (otherJob, _) = await AcceptedAsync();

        var unusable = new (string JobId, string ArtifactId)[]
        {
            (foreignJob, foreignArtifact.ArtifactId),
            ("job-ffffffffffffffffffffffffffffffff", artifact.ArtifactId),
            (ownJob, "artifact-ffffffffffffffffffffffffffffffff"),
            (otherJob, artifact.ArtifactId),
        };
        var malformed = new (string JobId, string ArtifactId)[]
        {
            ("job-XYZ", artifact.ArtifactId),
            ("job-0123456789abcdef0123456789abcde", artifact.ArtifactId),
            ("job-0123456789abcdef0123456789abcdef0", artifact.ArtifactId),
            ("job-0123456789abcdef0123456789abcdeF", artifact.ArtifactId),
            (ownJob, "artifact-XYZ"),
            (ownJob, "artifact-0123456789abcdef0123456789abcde"),
            (ownJob, "artifact-0123456789abcdef0123456789abcdef0"),
            (ownJob, "artifact-0123456789abcdef0123456789abcdeF"),
            (ownJob, "output-0123456789abcdef0123456789abcdef"),
        };

        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (jobId, artifactId) in unusable)
        {
            using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifactId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var answer = await JobApiHost.AnswerAsync(response);
            Assert.Equal(ErrorCodes.InputNotFound, answer.GetProperty("code").GetString());
            Assert.Equal(ArtifactDownloadEndpoint.NotFoundTitle, answer.GetProperty("title").GetString());
            Assert.Equal(ArtifactDownloadEndpoint.NotFoundDetail, answer.GetProperty("detail").GetString());
            problems[$"{jobId}/{artifactId}"] = await JobApiHost.ProblemAsync(response);
        }

        // A value that cannot be an identifier names an artifact that cannot exist: it is
        // answered without a read.
        var callsBefore = _host.Jobs.FindArtifactCalls;
        foreach (var (jobId, artifactId) in malformed)
        {
            using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifactId);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var answer = await JobApiHost.AnswerAsync(response);
            Assert.Equal(ErrorCodes.InputNotFound, answer.GetProperty("code").GetString());
            Assert.Equal(ArtifactDownloadEndpoint.NotFoundTitle, answer.GetProperty("title").GetString());
            problems[$"{jobId}/{artifactId}"] = await JobApiHost.ProblemAsync(response);
        }

        Assert.Equal(callsBefore, _host.Jobs.FindArtifactCalls);

        // One answer for every case, byte for byte apart from the trace identifier.
        Assert.Single(problems.Values.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnknownIdentityIsRefusedBeforeAnyReadAndAnInactiveConsumerDownloads()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(1_024));

        var readsBefore = _host.Jobs.FindArtifactCalls;
        foreach (var declared in new[] { (string?)null, "no-such-consumer" })
        {
            using var refused = await DownloadAsync(declared, jobId, artifact.ArtifactId);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(
                ErrorCodes.ConsumerUnknown,
                (await JobApiHost.AnswerAsync(refused)).GetProperty("code").GetString());
        }

        Assert.Equal(readsBefore, _host.Jobs.FindArtifactCalls);

        // Downloading is kept for a consumer that was made inactive: the file is still its own.
        var (inactiveJob, inactiveOutput) = await AcceptedAsync(JobApiHost.InactiveConsumer);
        var (inactiveArtifact, inactiveContent) = await PublishedAsync(inactiveJob, inactiveOutput, Body(2_048));
        using var response = await DownloadAsync(JobApiHost.InactiveConsumer, inactiveJob, inactiveArtifact.ArtifactId);
        var installed = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(inactiveContent, installed);
    }

    [Fact]
    public async Task AMissingObjectOfAnUnexpiredArtifactIsAServiceFault()
    {
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(1_024));
        File.Delete(PathOf(artifact.ObjectKey));
        _instruments.Reset();

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The artifact exists for this consumer and has not expired: the fault is the service's,
        // and "not found" or "expired" would both be false.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, _instruments.MissingObjects);
        Assert.DoesNotContain(artifact.ObjectKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_host.StorageRoot, body, StringComparison.Ordinal);
        Assert.DoesNotContain("FileNotFound", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StorageOutcome.NotFound, HttpStatusCode.InternalServerError)]
    [InlineData(StorageOutcome.Permanent, HttpStatusCode.InternalServerError)]
    [InlineData(StorageOutcome.IntegrityFailure, HttpStatusCode.InternalServerError)]
    [InlineData(StorageOutcome.AlreadyExists, HttpStatusCode.InternalServerError)]
    [InlineData(StorageOutcome.Transient, HttpStatusCode.ServiceUnavailable)]
    public async Task AnUnreadableStoredObjectIsAnsweredByTheOutcomeTheStorageGave(
        StorageOutcome outcome,
        HttpStatusCode expected)
    {
        var observed = ObserveStorage(outcome);
        var (jobId, outputId) = await AcceptedAsync();
        var (artifact, _) = await PublishedAsync(jobId, outputId, Body(1_024));
        _instruments.Reset();

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, artifact.ArtifactId);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, observed.Opens);
        Assert.Equal(1, _instruments.MissingObjects);
        Assert.Equal(0, _instruments.IntegrityFailures);
        Assert.DoesNotContain(artifact.ObjectKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("storage.io-error", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredKeyTheStoragePortRefusesIsAnsweredAsAnObjectTheServiceCannotRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, outputId) = await AcceptedAsync();
        await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken);

        // A row whose stored key is not a key at all: the row is unauthorised to name an object,
        // so the service answers its own fault rather than any door the caller could open.
        Assert.Equal(
            PublishOutcome.Published,
            (await _host.Jobs.PublishAsync(
                new ArtifactRecord(
                    ArtifactIdentifier.Create(),
                    jobId,
                    outputId,
                    OutputFormat.Pdf,
                    "application/pdf",
                    "Not A Key",
                    new string('a', 64),
                    1_024,
                    1,
                    Now,
                    Now.Add(Retention)),
                cancellationToken)).Outcome);
        var stored = _host.Jobs.ArtifactOf(outputId)!;
        _instruments.Reset();

        using var response = await DownloadAsync(JobApiHost.Consumer, jobId, stored.ArtifactId);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            ErrorCodes.ServiceUnavailable,
            (await JobApiHost.AnswerAsync(response)).GetProperty("code").GetString());
        Assert.Equal(1, _instruments.MissingObjects);
    }

    private async Task<(string JobId, string OutputId)> AcceptedAsync(string consumer = JobApiHost.Consumer)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await _host.UploadAsync(JobApiHost.UploadState.Ready, consumer);
        var acceptance = new JobAcceptance(
            JobIdentifier.Create(),
            [new RequestedOutput(JobIdentifier.CreateOutput(), OutputFormat.Pdf)],
            consumer,
            uploadId,
            "pilot-grouped",
            TemplateReferenceForm.Exact,
            "1.0.0",
            ReportService.Testing.AcceptanceIdentity.For(uploadId));
        Assert.IsType<JobAcceptanceResult.Accepted>(await _host.Jobs.AcceptAsync(acceptance, Now, cancellationToken));
        return (acceptance.ReportJobId, acceptance.Outputs[0].OutputId);
    }

    // Publishes the output's one artifact over content written through the storage port, so the
    // size and the hash the row carries are the store's own figures for those bytes.
    private async Task<(ArtifactRecord Artifact, byte[] Content)> PublishedAsync(
        string jobId,
        string outputId,
        byte[] content,
        int? pageCount = 3)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var objectKey = $"outputs/{jobId}/{outputId}/{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        using var source = new MemoryStream(content, writable: false);
        var written = await _host.Objects.WriteAsync(ObjectKey.Create(objectKey), source, cancellationToken);
        Assert.True(written.Succeeded, $"the case's content had to be stored: {written.Outcome} {written.Error?.Message}");

        Assert.Equal(TransitionOutcome.Applied, await _host.Jobs.TransitionOutputAsync(outputId, Now, cancellationToken));
        var artifact = new ArtifactRecord(
            ArtifactIdentifier.Create(),
            jobId,
            outputId,
            OutputFormat.Pdf,
            "application/pdf",
            objectKey,
            written.Value!.Sha256,
            written.Value.Size,
            pageCount,
            Now,
            Now.Add(Retention));
        Assert.Equal(PublishOutcome.Published, (await _host.Jobs.PublishAsync(artifact, cancellationToken)).Outcome);
        Assert.Equal(JobFinalizationOutcome.Applied, (await _host.Jobs.FinalizeJobAsync(jobId, Now, cancellationToken)).Outcome);
        return (artifact, content);
    }

    // Publishes a succeeded HTML output's one artifact over content written through the storage
    // port. The job is accepted (which creates its PDF output), then an independent HTML output is
    // seeded and its .html published with the HTML media type — so the row the serve path reads
    // carries OutputFormat.Html and text/html; charset=utf-8, as a produced HTML output would.
    private async Task<(string JobId, ArtifactRecord Artifact, byte[] Content)> PublishedHtmlAsync(byte[] content)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (jobId, _) = await AcceptedAsync();
        var htmlOutputId = JobIdentifier.CreateOutput();
        _host.Jobs.SeedOutput(jobId, htmlOutputId, OutputFormat.Html);

        var objectKey = $"outputs/{jobId}/{htmlOutputId}/{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}";
        using var source = new MemoryStream(content, writable: false);
        var written = await _host.Objects.WriteAsync(ObjectKey.Create(objectKey), source, cancellationToken);
        Assert.True(written.Succeeded, $"the case's content had to be stored: {written.Outcome} {written.Error?.Message}");

        var artifact = new ArtifactRecord(
            ArtifactIdentifier.Create(),
            jobId,
            htmlOutputId,
            OutputFormat.Html,
            "text/html; charset=utf-8",
            objectKey,
            written.Value!.Sha256,
            written.Value.Size,
            null,
            Now,
            Now.Add(Retention));
        Assert.Equal(PublishOutcome.Published, (await _host.Jobs.PublishAsync(artifact, cancellationToken)).Outcome);
        return (jobId, artifact, content);
    }

    // Replaces the host with one whose stored objects are counted, or whose reads are answered by
    // one of the port's failure outcomes; the case then arranges and downloads through it.
    private ObservedStore ObserveStorage(StorageOutcome? refusal = null)
    {
        ObservedStore? observed = null;
        _host.Dispose();
        _host = new JobApiHost(objects: inner => observed = new ObservedStore(inner, refusal), meterName: _meterName);
        return observed!;
    }

    private Task<HttpResponseMessage> DownloadAsync(string? consumerId, string jobId, string artifactId) =>
        _host.Client.SendAsync(
            DownloadRequest(consumerId, jobId, artifactId),
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

    private static HttpRequestMessage DownloadRequest(string? consumerId, string jobId, string artifactId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                $"{CreateReportJobEndpoint.RoutePrefix}/{jobId}{ArtifactListEndpoint.RouteSuffix}/{artifactId}{ArtifactDownloadEndpoint.RouteSuffix}",
                UriKind.Relative));
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }

        return message;
    }

    private static HttpRequestMessage ListRequest(string consumerId, string jobId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{CreateReportJobEndpoint.RoutePrefix}/{jobId}{ArtifactListEndpoint.RouteSuffix}", UriKind.Relative));
        message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        return message;
    }

    // Reads the body the way a client does: the declared length is known before the first byte,
    // and a transfer the service cut raises on the read that finds the connection gone.
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

    private static string HashOf(byte[] content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));

    private string PathOf(string objectKey) =>
        Path.Combine([_host.StorageRoot, .. ObjectKey.Create(objectKey).Segments]);

    private static void Corrupt(string path, string corruption)
    {
        var bytes = File.ReadAllBytes(path);
        byte[] altered = corruption switch
        {
            "flipped" => Flip(bytes),
            "missing" => bytes[..^1],
            "extra" => [.. bytes, (byte)0x7f],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "unknown corruption"),
        };
        File.WriteAllBytes(path, altered);
    }

    // A same-size corruption: the object is as long as the row says and hashes to something else.
    private static byte[] Flip(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[copy.Length / 2] ^= 0x5a;
        return copy;
    }

    private static byte[] Body(int length)
    {
        var content = new byte[length];
        for (var index = 0; index < content.Length; index++)
        {
            content[index] = (byte)(index % 251);
        }

        return content;
    }

    private static PartitionRecord Partition(string jobId, long recordCount) => new(
        jobId,
        1,
        1,
        recordCount,
        24,
        $"index/{jobId}/0/attempt",
        "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3",
        120,
        Now);

    // Forwards every member to the store it wraps, counting the reads of stored objects and, when
    // it is told to, answering one of the port's failure outcomes instead of reading.
    private sealed class ObservedStore(IObjectStore inner, StorageOutcome? refusal = null) : IObjectStore
    {
        private int _opens;

        /// <summary>How many times a stored object was opened for a read.</summary>
        public int Opens => Volatile.Read(ref _opens);

        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageResult<ObjectWrite>> WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken) =>
            inner.WriteAsync(key, content, cancellationToken);

        public Task<StorageResult<VerifiedRead>> OpenReadAsync(
            ObjectKey key,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _opens);
            return refusal is { } outcome
                ? Task.FromResult(StorageResult.Failure<VerifiedRead>(
                    outcome,
                    new StorageError(StorageErrorCodes.IoError, "the object could not be read")))
                : inner.OpenReadAsync(key, expectedSize, expectedSha256, cancellationToken);
        }

        public Task<StorageResult<ObjectInfo>> GetInfoAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.GetInfoAsync(key, cancellationToken);

        public Task<StorageResult<bool>> DeleteAsync(ObjectKey key, CancellationToken cancellationToken) =>
            inner.DeleteAsync(key, cancellationToken);

        public IAsyncEnumerable<ObjectInfo> ListAsync(string classPrefix, CancellationToken cancellationToken) =>
            inner.ListAsync(classPrefix, cancellationToken);
    }
}
