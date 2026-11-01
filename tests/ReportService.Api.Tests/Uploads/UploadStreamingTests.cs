using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.ServiceDefaults;
using ReportService.Storage;
using ReportService.Testing;
using ReportService.Uploads;
using Xunit;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The body reader: what it counts, what it refuses, and what it records before it
/// raises. Every case is decided on a stream the test controls, so a case says
/// exactly how the body ended.
/// </summary>
public sealed class UploadBodyStreamTests
{
    [Fact]
    public async Task ExactlyTheLimitIsAcceptedAndOneMoreIsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var accepted = new UploadBodyStream(new MemoryStream(new byte[64]), 64, 64))
        {
            await accepted.CopyToAsync(new MemoryStream(), cancellationToken);
        }

        var reader = new UploadBodyStream(new MemoryStream(new byte[65]), 64, 65);
        await Assert.ThrowsAsync<UploadQuotaExceededException>(
            () => reader.CopyToAsync(new MemoryStream(), cancellationToken));

        Assert.Equal(UploadStreamFailure.QuotaExceeded, reader.RecordedFailure);
        Assert.Equal(65, reader.BytesRead);
    }

    [Fact]
    public async Task ADeclaredLengthShortfallIsAnInterruption()
    {
        var reader = new UploadBodyStream(new MemoryStream(new byte[10]), 64, declaredLength: 11);

        await Assert.ThrowsAsync<UploadTransferInterruptedException>(
            () => reader.CopyToAsync(new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(UploadStreamFailure.Interrupted, reader.RecordedFailure);
        Assert.Equal(10, reader.BytesRead);
    }

    [Fact]
    public async Task ATransportFailureIsAnInterruption()
    {
        var reader = new UploadBodyStream(new FailingStream(), 64, null);

        await Assert.ThrowsAsync<UploadTransferInterruptedException>(
            () => reader.CopyToAsync(new MemoryStream(), TestContext.Current.CancellationToken));

        Assert.Equal(UploadStreamFailure.Interrupted, reader.RecordedFailure);
    }

    [Fact]
    public async Task AnAbortedRequestIsAnInterruption()
    {
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var reader = new UploadBodyStream(new BodyThatHonoursCancellation(), 64, null);

        await Assert.ThrowsAsync<UploadTransferInterruptedException>(
            () => reader.CopyToAsync(new MemoryStream(), abort.Token));

        Assert.Equal(UploadStreamFailure.Interrupted, reader.RecordedFailure);
    }

    [Fact]
    public async Task ABodyWithoutADeclaredLengthEndsNormally()
    {
        var reader = new UploadBodyStream(new MemoryStream(new byte[10]), 64, null);

        await reader.CopyToAsync(new MemoryStream(), TestContext.Current.CancellationToken);

        Assert.Equal(UploadStreamFailure.None, reader.RecordedFailure);
        Assert.Equal(10, reader.BytesRead);
    }

    [Fact]
    public void TheReaderIsForwardOnlyAndReadOnly()
    {
        using var reader = new UploadBodyStream(new MemoryStream(), 64, null);

        Assert.True(reader.CanRead);
        Assert.False(reader.CanSeek);
        Assert.False(reader.CanWrite);
        Assert.Throws<NotSupportedException>(() => reader.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => reader.SetLength(1));
        Assert.Throws<NotSupportedException>(() => reader.Write([], 0, 0));
        Assert.Throws<NotSupportedException>(() => _ = reader.Length);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the client is gone.");

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    private sealed class BodyThatHonoursCancellation : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}

/// <summary>The encoding declaration: three values, and the header's text never kept.</summary>
public sealed class ContentEncodingHeaderTests
{
    [Fact]
    public void AnAbsentHeaderDeclaresNoEncoding() =>
        Assert.Equal(UploadContentEncoding.None, Classify());

    [Theory]
    [InlineData("identity", UploadContentEncoding.None)]
    [InlineData("IDENTITY", UploadContentEncoding.None)]
    [InlineData("gzip", UploadContentEncoding.Gzip)]
    [InlineData("GZIP", UploadContentEncoding.Gzip)]
    [InlineData("gzip, gzip", UploadContentEncoding.Other)]
    [InlineData("br", UploadContentEncoding.Other)]
    [InlineData("deflate", UploadContentEncoding.Other)]
    public void EveryDeclarationIsClassified(string value, UploadContentEncoding expected) =>
        Assert.Equal(expected, Classify(value));

    [Fact]
    public void TwoHeadersAreNotOneDeclaration() =>
        Assert.Equal(UploadContentEncoding.Other, Classify("gzip", "gzip"));

    private static UploadContentEncoding Classify(params string[] values)
    {
        var context = new DefaultHttpContext();
        foreach (var value in values)
        {
            context.Request.Headers.Append(ContentEncodingHeader.HeaderName, value);
        }

        return ContentEncodingHeader.Classify(context.Request);
    }
}

/// <summary>
/// The confirmation reader: an absent body declares nothing, an empty object is
/// valid, and every other shape is refused with the contract's reason, field and
/// constant detail — never a value the caller sent.
/// </summary>
public sealed class CompleteUploadRequestReaderTests
{
    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

    [Fact]
    public async Task NoBodyDeclaresNothing()
    {
        var result = await ReadAsync(null, null);

        Assert.Null(Assert.IsType<CompleteUploadRequestReader.Result.Accepted>(result).Sha256);
    }

    [Fact]
    public async Task AnEmptyObjectDeclaresNothing()
    {
        var result = await ReadAsync("{}", CompleteUploadRequestReader.JsonMediaType);

        Assert.Null(Assert.IsType<CompleteUploadRequestReader.Result.Accepted>(result).Sha256);
    }

    [Theory]
    [InlineData(Hash)]
    [InlineData("9FFCBF0ACEDFE4529A6093C4C669EFAB29C2D523A7794BACFB8EB8A946CA8DD3")]
    public async Task ADeclaredHashIsReadInLowerCase(string declared)
    {
        var result = await ReadAsync($$"""{"sha256":"{{declared}}"}""", CompleteUploadRequestReader.JsonMediaType);

        Assert.Equal(Hash, Assert.IsType<CompleteUploadRequestReader.Result.Accepted>(result).Sha256);
    }

    [Theory]
    [InlineData("""{"sha256":null}""", "null-field", "sha256", "The field 'sha256' must not be null.")]
    [InlineData("""{"sha256":123}""", "wrong-type", "sha256", "The field 'sha256' must be a string.")]
    [InlineData("""{"sha256":"abc"}""", "invalid-format", "sha256", "The field 'sha256' must be 64 hexadecimal characters.")]
    [InlineData("""{"sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd"}""", "invalid-format", "sha256", "The field 'sha256' must be 64 hexadecimal characters.")]
    [InlineData("""{"sha256":"zzfcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3"}""", "invalid-format", "sha256", "The field 'sha256' must be 64 hexadecimal characters.")]
    [InlineData("""{"sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3","sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3"}""", "duplicate-field", "sha256", "The field 'sha256' appears more than once.")]
    [InlineData("""{"sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3","planted":1}""", "unknown-field", null, "The request contains 1 field that is not part of this operation.")]
    [InlineData(""" {"sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3"} {}""", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("not json", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("""{"planted":1,"sha256":"abc"}""", "unknown-field", null, "The request contains 1 field that is not part of this operation.")]
    public async Task AMalformedDocumentIsRefusedByItsFirstCondition(
        string body,
        string reason,
        string? field,
        string detail)
    {
        var result = await ReadAsync(body, CompleteUploadRequestReader.JsonMediaType);
        var problem = Assert.IsType<CompleteUploadRequestReader.Result.Rejected>(result).Problem;

        Assert.Equal(reason, problem.Reason);
        Assert.Equal(field, problem.Field);
        Assert.Equal(detail, problem.Detail);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsRefusedByItsMediaType()
    {
        var result = await ReadAsync($$"""{"sha256":"{{Hash}}"}""", "text/plain");
        var problem = Assert.IsType<CompleteUploadRequestReader.Result.Rejected>(result).Problem;

        Assert.Equal(RequestInvalidDetails.MediaTypeReason, problem.Reason);
        Assert.Null(problem.Field);
    }

    [Fact]
    public async Task ABodyOverTheBoundIsRefusedWithoutBeingRead()
    {
        var result = await ReadAsync(new string(' ', CompleteUploadRequestReader.MaxBodyBytes + 1), CompleteUploadRequestReader.JsonMediaType, declareLength: true);

        Assert.IsType<CompleteUploadRequestReader.Result.TooLarge>(result);
    }

    [Fact]
    public async Task ABodyWithAByteOrderMarkIsRead()
    {
        var body = "\uFEFF" + $$"""{"sha256":"{{Hash}}"}""";
        var result = await ReadAsync(body, CompleteUploadRequestReader.JsonMediaType);

        Assert.Equal(Hash, Assert.IsType<CompleteUploadRequestReader.Result.Accepted>(result).Sha256);
    }

    [Fact]
    public async Task AValueTheCallerSentIsNeverEchoedByTheReader()
    {
        var planted = "planted-value-" + new string('x', 48);
        var result = await ReadAsync($$"""{"sha256":"{{planted}}"}""", CompleteUploadRequestReader.JsonMediaType);
        var problem = Assert.IsType<CompleteUploadRequestReader.Result.Rejected>(result).Problem;

        Assert.Equal(RequestInvalidDetails.InvalidFormatReason, problem.Reason);
        Assert.DoesNotContain(planted, problem.Detail, StringComparison.Ordinal);
    }

    private static async Task<CompleteUploadRequestReader.Result> ReadAsync(
        string? body,
        string? contentType,
        bool declareLength = true)
    {
        var context = new DefaultHttpContext();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes, writable: false);
            if (declareLength)
            {
                context.Request.ContentLength = bytes.Length;
            }

            context.Request.ContentType = contentType;
        }

        return await CompleteUploadRequestReader.ReadAsync(context.Request, TestContext.Current.CancellationToken);
    }
}

/// <summary>The status projection: the whole field set, and every member null while it does not apply.</summary>
public sealed class UploadStatusResponseTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    [Fact]
    public void ANewSessionShowsOnlyItsIdentityAndItsLifetime()
    {
        var response = UploadStatusResponse.From(Record());

        Assert.Equal("created", response.Status);
        Assert.Equal("2026-11-01T04:39:23.123Z", response.CreatedAt);
        Assert.Equal("2026-11-02T04:39:23.123Z", response.ExpiresAt);
        Assert.Null(response.ReceivedBytes);
        Assert.Null(response.CompletedAt);
        Assert.Null(response.Sha256);
        Assert.Null(response.SizeBytes);
        Assert.Null(response.RejectionCode);
        Assert.Null(response.RejectionReason);
    }

    [Fact]
    public void AReadySessionShowsTheContentPairOfItsSnapshot()
    {
        var record = Record(UploadStatus.Ready) with
        {
            ReceivedBytes = 129,
            CompletedAt = CreatedAt.AddMinutes(1),
            Snapshot = new SnapshotRecord(
                UploadId,
                "dev-consumer",
                ObjectKey.FromSegments("snapshots", UploadId, "attempt"),
                UploadContentEncoding.Gzip,
                DataProfile.HeaderItemsV1,
                new string('a', 64),
                322,
                new string('b', 64),
                129,
                CreatedAt),
        };

        var response = UploadStatusResponse.From(record);

        Assert.Equal(129, response.ReceivedBytes);
        Assert.Equal("2026-11-01T04:40:23.123Z", response.CompletedAt);
        Assert.Equal(new string('a', 64), response.Sha256);
        Assert.Equal(322, response.SizeBytes);
        Assert.Null(response.RejectionCode);
    }

    [Fact]
    public void ARejectedSessionShowsItsCodeAndReasonAndNoSnapshot()
    {
        var record = Record(UploadStatus.Rejected) with
        {
            ReceivedBytes = 129,
            RejectionCode = UploadRejectionCode.DataInvalid,
            RejectionReason = UploadRejectionReason.Truncated,
        };

        var response = UploadStatusResponse.From(record);

        Assert.Equal("DATA_INVALID", response.RejectionCode);
        Assert.Equal("truncated", response.RejectionReason);
        Assert.Null(response.Sha256);
        Assert.Null(response.SizeBytes);
    }

    private const string UploadId = "upload-0123456789abcdef0123456789abcdef";

    private static UploadRecord Record(UploadStatus status = UploadStatus.Created) =>
        new(
            UploadId,
            "dev-consumer",
            UploadMode.ApiStream,
            DataProfile.HeaderItemsV1,
            status,
            ObjectKey.FromSegments("staging", UploadId, "content"),
            CreatedAt,
            CreatedAt.AddDays(1));
}

/// <summary>
/// The two operations through the API's real pipeline with the record store
/// replaced by the in-memory one, the storage port real on a temporary root and the
/// clock fixed: what a body does as it arrives, what the confirmation decides, and
/// every refusal — each of which leaves the upload in a state the consumer can read.
/// </summary>
public sealed class UploadContentEndpointsTests : IDisposable
{
    private const string Consumer = "dev-consumer";
    private const string OtherConsumer = "other-consumer";
    private const string PlantedSecret = "planted-secret-4b73f1";

    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero).AddTicks(4567);

    private readonly TemporaryDirectory _directory = new();
    private readonly InMemoryUploadStore _store = new();
    private readonly LogCapture _logs = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _logs.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task ABodyIsStreamedStoredAndCounted()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"items":[]}""");

        using var response = await client.SendAsync(SendRequest([Consumer], uploadId, body), cancellationToken);
        var projection = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("uploading", projection.GetProperty("status").GetString());
        Assert.Equal(body.Length, projection.GetProperty("receivedBytes").GetInt64());

        var record = Assert.Single(_store.Records);
        Assert.Equal(UploadContentOutcome.Complete, record.ContentOutcome);
        Assert.Equal(UploadContentEncoding.None, record.ContentEncoding);
        Assert.Equal(body.Length, record.ReceivedBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)), record.StagingSha256);
        Assert.Contains(_logs.Lines, line => line.Contains("content received", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeclaredLengthOverTheLimitIsRefusedWithoutReadingTheBody()
    {
        var client = Client(maxStoredBytes: "64");
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(SendRequest([Consumer], uploadId, new byte[65]), cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(413, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, problem.GetProperty("code").GetString());
        Assert.Equal("close", response.Headers.Connection.FirstOrDefault());

        var record = Assert.Single(_store.Records);
        Assert.Equal(UploadStatus.Rejected, record.Status);
        Assert.Equal(UploadRejectionReason.StoredBytes, record.RejectionReason);
        Assert.Equal(0, record.ReceivedBytes);
    }

    [Fact]
    public async Task ABodyOverTheLimitWithoutADeclaredLengthIsStoppedAtTheLimit()
    {
        var client = Client(maxStoredBytes: "64");
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(SendRequest([Consumer], uploadId, new byte[4096], declareLength: false), cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(413, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, problem.GetProperty("code").GetString());

        var record = Assert.Single(_store.Records);
        Assert.Equal(UploadStatus.Rejected, record.Status);
        Assert.NotNull(record.ReceivedBytes);
        Assert.InRange(record.ReceivedBytes!.Value, 0, 64 + (16 * 1024));
    }

    [Fact]
    public async Task ASecondBodyIsRefusedAndTheFirstObjectIsUnchanged()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"items":[]}""");
        using (var first = await client.SendAsync(SendRequest([Consumer], uploadId, body), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var hashBefore = Assert.Single(_store.Records).StagingSha256;

        using var second = await client.SendAsync(SendRequest([Consumer], uploadId, body), cancellationToken);
        var problem = await JsonAsync(second, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.UploadStateConflict, problem.GetProperty("code").GetString());
        Assert.Equal(UploadContentEndpoint.StateConflictDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(hashBefore, Assert.Single(_store.Records).StagingSha256);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsRefusedByItsMediaType()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(
            SendRequest([Consumer], uploadId, Encoding.UTF8.GetBytes("hello"), contentType: "text/plain"),
            cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal("media-type", problem.GetProperty("reason").GetString());
        Assert.Equal(UploadStatus.Created, Assert.Single(_store.Records).Status);
    }

    [Theory]
    [InlineData("GZIP")]
    [InlineData("br")]
    public async Task EveryEncodingIsRecordedInItsOwnClassification(string declared)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(
            SendRequest([Consumer], uploadId, Encoding.UTF8.GetBytes("""{"items":[]}"""), contentEncoding: declared),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = declared is "GZIP" ? UploadContentEncoding.Gzip : UploadContentEncoding.Other;
        Assert.Equal(expected, Assert.Single(_store.Records).ContentEncoding);
    }

    [Fact]
    public async Task AForeignUploadIsAnsweredExactlyAsAnAbsentOne()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);
        var readsBeforeMalformed = _store.FindCalls;

        using var foreign = await client.SendAsync(SendRequest([OtherConsumer], uploadId, new byte[1]), cancellationToken);
        using var malformed = await client.SendAsync(SendRequest([Consumer], "not-an-identifier", new byte[1]), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(ErrorCodes.InputNotFound, (await JsonAsync(foreign, cancellationToken)).GetProperty("code").GetString());
        Assert.Equal(readsBeforeMalformed + 1, _store.FindCalls);
    }

    [Fact]
    public async Task ADeclaredEncodingValueIsNeverLoggedOrEchoed()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(
            SendRequest([Consumer], uploadId, Encoding.UTF8.GetBytes("""{"items":[]}"""), contentEncoding: PlantedSecret),
            cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(PlantedSecret, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(PlantedSecret, StringComparison.Ordinal));
        Assert.Contains(_logs.Lines, line => line.Contains("content received", StringComparison.Ordinal) && line.Contains("other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoIdentityIsRefusedBeforeTheBodyIsRead()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);
        var readsBefore = _store.FindCalls;

        using var response = await client.SendAsync(SendRequest([], uploadId, new byte[1]), cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(readsBefore, _store.FindCalls);
    }

    private HttpClient Client(string? maxStoredBytes = null, IObjectStore? objects = null)
    {
        var registry = _directory.Write(
            "consumers.json",
            """{ "consumers": [ { "consumerId": "dev-consumer", "active": true } ] }""");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            if (maxStoredBytes is not null)
            {
                builder.UseSetting($"{UploadOptions.SectionPath}:{UploadOptions.MaxStoredBytesSetting}", maxStoredBytes);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<IUploadStore>();
                services.AddSingleton<IUploadStore>(_store);
                if (objects is not null)
                {
                    services.RemoveAll<IObjectStore>();
                    services.AddSingleton(objects);
                }

                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        });
        _disposables.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<string> CreateAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(
            CreateUpload("""{"profile":"header-items-v1","mode":"API_STREAM"}"""),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var projection = JsonDocument.Parse(body);
        return projection.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static HttpRequestMessage CreateUpload(string body) =>
        new(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
            Headers = { { ConsumerIdentity.HeaderName, Consumer } },
        };

    private static HttpRequestMessage SendRequest(
        string[] identities,
        string uploadId,
        byte[] body,
        bool declareLength = true,
        string contentType = CreateUploadRequestReader.JsonMediaType,
        string? contentEncoding = null)
    {
        HttpContent content = declareLength ? new ByteArrayContent(body) : new ChunkedContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var message = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = content,
        };
        if (contentEncoding is not null)
        {
            content.Headers.ContentEncoding.Add(contentEncoding);
        }

        foreach (var identity in identities)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, identity);
        }

        return message;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>A body sent without a declared length, so the service has to count it.</summary>
    private sealed class ChunkedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
