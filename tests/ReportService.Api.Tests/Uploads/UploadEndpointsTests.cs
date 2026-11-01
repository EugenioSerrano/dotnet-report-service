using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Uploads;
using ReportService.Uploads;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The two upload operations through the API's real pipeline, with the record
/// store replaced by the in-memory one and the clock fixed: the session a consumer
/// opens, the status it reads back, and every refusal the operation gives — each
/// of which must leave no session behind. The decisions of the declared identity
/// come before the body and the body before the support check, so the answer of a
/// request with several faults is always the same one.
/// </summary>
public sealed class UploadEndpointsTests : IDisposable
{
    private const string Consumer = "dev-consumer";
    private const string OtherConsumer = "other-consumer";
    private const string PlantedSecret = "planted-secret-3f8a21";
    private const string ValidBody = """{"profile":"header-items-v1","mode":"API_STREAM"}""";
    private const string CreatedAtWire = "2026-11-01T04:39:23.123Z";
    private const string ExpiresAtWire = "2026-11-02T04:39:23.123Z";

    // A fixed instant with sub-millisecond ticks: the response must show whole milliseconds.
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
    public async Task CreatingASessionAnswersCreatedWithItsStatusProjectionAndRecordsTheSession()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(CreateRequest(), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var projection = JsonDocument.Parse(body);
        var root = projection.RootElement;
        var uploadId = root.GetProperty("uploadId").GetString();
        Assert.NotNull(uploadId);
        Assert.Matches("^upload-[0-9a-f]{32}$", uploadId);
        Assert.Equal($"{UploadEndpoints.RoutePrefix}/{uploadId}", response.Headers.Location?.OriginalString);
        Assert.Equal("created", root.GetProperty("status").GetString());
        Assert.Equal("API_STREAM", root.GetProperty("mode").GetString());
        Assert.Equal("header-items-v1", root.GetProperty("profile").GetString());
        Assert.Equal(CreatedAtWire, root.GetProperty("createdAt").GetString());
        Assert.Equal(ExpiresAtWire, root.GetProperty("expiresAt").GetString());
        Assert.Equal(6, root.EnumerateObject().Count());

        var record = Assert.Single(_store.Records);
        Assert.Equal(uploadId, record.UploadId);
        Assert.Equal(Consumer, record.ConsumerId);
        Assert.Equal(UploadMode.ApiStream, record.Mode);
        Assert.Equal(DataProfile.HeaderItemsV1, record.Profile);
        Assert.Equal(UploadStatus.Created, record.Status);
        Assert.Equal($"staging/{uploadId}/content", record.StagingKey.ToString());
        Assert.Equal(UploadWireValues.TruncateToMillisecond(Now), record.CreatedAt);
        Assert.Equal(1, _store.CreateCalls);
        Assert.Contains(_logs.Lines, line => line.Contains(uploadId!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoCreationsAnswerTwoDifferentIdentifiers()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await CreateAsync(client, cancellationToken);
        var second = await CreateAsync(client, cancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(2, _store.Records.Count);
    }

    [Fact]
    public async Task TheCreatorReadsTheSameProjectionBack()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var created = await client.SendAsync(CreateRequest(), cancellationToken);
        var createdBody = await created.Content.ReadAsStringAsync(cancellationToken);
        using var projection = JsonDocument.Parse(createdBody);
        var uploadId = projection.RootElement.GetProperty("uploadId").GetString()!;

        using var response = await client.SendAsync(GetRequest([Consumer], uploadId), cancellationToken);
        var readBody = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(createdBody, readBody);
    }

    [Theory]
    [InlineData("DIRECT_STORAGE")]
    [InlineData("FTP")]
    [InlineData("api_stream")]
    [InlineData("")]
    public async Task AnUnsupportedModeIsRefusedSynchronouslyAndNothingIsCreated(string mode)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sent = $$"""{"profile":"header-items-v1","mode":"{{mode}}"}""";

        using var response = await client.SendAsync(CreateRequest([Consumer], Json(sent)), cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.Equal("Unsupported capability", problem.GetProperty("title").GetString());
        Assert.Equal(UploadEndpoints.UnsupportedModeDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(0, _store.CreateCalls);
        Assert.Empty(_store.Records);
    }

    [Theory]
    [InlineData("header-items-v2")]
    [InlineData("HEADER_ITEMS_V1")]
    [InlineData("root-array")]
    [InlineData("")]
    public async Task AnUnsupportedProfileIsRefusedSynchronouslyAndNothingIsCreated(string profile)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var sent = $$"""{"profile":"{{profile}}","mode":"API_STREAM"}""";

        using var response = await client.SendAsync(CreateRequest([Consumer], Json(sent)), cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.UnsupportedCapability, problem.GetProperty("code").GetString());
        Assert.Equal(UploadEndpoints.UnsupportedProfileDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task BothUnsupportedValuesAreRefusedInOneAnswerNamingModeFirst()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(
            CreateRequest([Consumer], Json("""{"profile":"root-array","mode":"DIRECT_STORAGE"}""")),
            cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal(
            UploadEndpoints.UnsupportedModeDetail + " " + UploadEndpoints.UnsupportedProfileDetail,
            problem.GetProperty("detail").GetString());
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task NoIdentityIsRefusedAndTheBodyIsNotRead()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        // A body the reader would refuse as too large: the identity is decided before it, so the
        // answer is the identity's and the store is never touched.
        using var response = await client.SendAsync(CreateRequest([], Json(ValidBody + new string(' ', 20_000))), cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
        Assert.Equal("Unknown consumer", problem.GetProperty("title").GetString());
        Assert.Equal(UploadEndpoints.ConsumerUnknownDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(0, _store.CreateCalls);
        Assert.Equal(0, _store.FindCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev consumer")]
    [InlineData("dev/consumer")]
    [InlineData("cónsumer")]
    public async Task AnUnusableIdentityIsRefusedOnBothOperations(string? identity)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var identities = identity is null ? Array.Empty<string>() : new[] { identity };

        using var created = await client.SendAsync(CreateRequest(identities, Json(ValidBody)), cancellationToken);
        using var read = await client.SendAsync(GetRequest(identities, "upload-0123456789abcdef0123456789abcdef"), cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, (await ProblemAsync(created, cancellationToken)).GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.ConsumerUnknown, (await ProblemAsync(read, cancellationToken)).GetProperty("code").GetString());
        Assert.Equal(0, _store.CreateCalls);
        Assert.Equal(0, _store.FindCalls);
    }

    [Fact]
    public async Task ARepeatedOrJoinedIdentityHeaderIsRefused()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var repeated = await client.SendAsync(CreateRequest([Consumer, OtherConsumer], Json(ValidBody)), cancellationToken);
        using var joined = await client.SendAsync(CreateRequest([$"{Consumer},{OtherConsumer}"], Json(ValidBody)), cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, joined.StatusCode);
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task AWellFormedIdentityThatThisIncrementDoesNotCheckIsAccepted()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(CreateRequest(["an-unregistered-consumer"], Json(ValidBody)), cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("an-unregistered-consumer", Assert.Single(_store.Records).ConsumerId);
    }

    [Theory]
    [InlineData("""{"profile":"header-items-v1"}""", "application/json", "missing-field", "mode", "The field 'mode' is required.")]
    [InlineData("""{"mode":"API_STREAM"}""", "application/json", "missing-field", "profile", "The field 'profile' is required.")]
    [InlineData("""{"profile":"header-items-v1","mode":null}""", "application/json", "null-field", "mode", "The field 'mode' must not be null.")]
    [InlineData("""{"profile":"header-items-v1","mode":12}""", "application/json", "wrong-type", "mode", "The field 'mode' must be a string.")]
    [InlineData("""{"profile":"header-items-v1","mode":{"a":"b"}}""", "application/json", "wrong-type", "mode", "The field 'mode' must be a string.")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM","mode":"API_STREAM"}""", "application/json", "duplicate-field", "mode", "The field 'mode' appears more than once.")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM","sha256":"x"}""", "application/json", "unknown-field", null, "The request contains 1 field that is not part of this operation.")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM","sha256":"x","other":"y","third":"z"}""", "application/json", "unknown-field", null, "The request contains 3 fields that are not part of this operation.")]
    [InlineData("not json", "application/json", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("[]", "application/json", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM"} {}""", "application/json", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("", "application/json", "malformed-json", null, "The request body must be a single JSON object.")]
    [InlineData("""{"profile":"header-items-v1","mode":"API_STREAM"}""", "text/plain", "media-type", null, "The request body must be sent as application/json.")]
    [InlineData("""{"profile":"header-items-v1","sha256":"x"}""", "application/json", "unknown-field", null, "The request contains 1 field that is not part of this operation.")]
    [InlineData("""{"profile":null,"mode":"API_STREAM","mode":"API_STREAM"}""", "application/json", "duplicate-field", "mode", "The field 'mode' appears more than once.")]
    public async Task AMalformedRequestIsRefusedWithTheFieldTheReasonAndTheConstantDetail(
        string body,
        string contentType,
        string reason,
        string? field,
        string detail)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(CreateRequest([Consumer], new StringContent(body, Encoding.UTF8, contentType)), cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(RequestInvalidDetails.Title, problem.GetProperty("title").GetString());
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
        Assert.Equal(reason, problem.GetProperty("reason").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task TheUnknownMemberCountIsTheOnlyThingSaidAboutUnknownMembers()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var planted = new[]
        {
            "<script>alert(1)</script>",
            "<script>",
            "é",
            new string('n', 1_000),
            PlantedSecret,
        };
        var body = $$"""
            {"profile":"header-items-v1","mode":"API_STREAM","{{planted[0]}}":1,"{{planted[1]}}":2,"{{planted[2]}}":3,"{{planted[3]}}":4,"{{planted[4]}}":5}
            """;

        using var response = await client.SendAsync(CreateRequest([Consumer], Json(body)), cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);
        var problem = JsonDocument.Parse(answer).RootElement;

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal("unknown-field", problem.GetProperty("reason").GetString());
        Assert.Equal(planted.Length, problem.GetProperty("unknownFieldCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, problem.GetProperty("field").ValueKind);
        foreach (var name in planted)
        {
            Assert.DoesNotContain(name, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(name, Headers(response), StringComparison.Ordinal);
            Assert.DoesNotContain(_logs.Lines, line => line.Contains(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AValueTheCallerSentIsNeverEchoedInTheAnswerOrTheLogs()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(
            CreateRequest([Consumer], Json("""{"profile":"header-items-v1","mode":{"planted":"planted-value-123"},"sha256":12345678901}""")),
            cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(400, (int)response.StatusCode);
        Assert.DoesNotContain("planted-value-123", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("planted", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("planted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains("12345678901", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACreationBodyOverTheBoundIsRefusedAsTooLarge(bool withContentLength)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = Encoding.UTF8.GetBytes(ValidBody + new string(' ', CreateUploadRequestReader.MaxBodyBytes + 1 - ValidBody.Length));
        HttpContent content = withContentLength ? new ByteArrayContent(body) : new ChunkedContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType);

        using var response = await client.SendAsync(CreateRequest([Consumer], content), cancellationToken);
        var problem = await ProblemAsync(response, cancellationToken);

        Assert.Equal(413, (int)response.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, problem.GetProperty("code").GetString());
        Assert.Equal("Request too large", problem.GetProperty("title").GetString());
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task ACreationBodyExactlyAtTheBoundIsAccepted()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var body = ValidBody + new string(' ', CreateUploadRequestReader.MaxBodyBytes - ValidBody.Length);

        using var response = await client.SendAsync(CreateRequest([Consumer], Json(body)), cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, _store.CreateCalls);
    }

    [Fact]
    public async Task AnUnknownMalformedAndForeignIdentifierGetTheSameNotFoundAnswer()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var foreign = await CreateAsync(client, cancellationToken);

        var unknown = await GetAsync(client, Consumer, "upload-44444444444444444444444444444444", cancellationToken);
        var foreignAnswer = await GetAsync(client, OtherConsumer, foreign, cancellationToken);
        var readsBeforeMalformed = _store.FindCalls;
        var malformed = await GetAsync(client, Consumer, "not-an-identifier", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal(HttpStatusCode.NotFound, foreignAnswer.Status);
        Assert.Equal(HttpStatusCode.NotFound, malformed.Status);
        Assert.Equal(Normalize(unknown.Body), Normalize(foreignAnswer.Body));
        Assert.Equal(Normalize(unknown.Body), Normalize(malformed.Body));
        Assert.Equal(ErrorCodes.InputNotFound, unknown.Body.GetProperty("code").GetString());
        Assert.Equal("Upload not found", unknown.Body.GetProperty("title").GetString());
        Assert.NotEqual(unknown.TraceId, malformed.TraceId);

        // Two real lookups (the unknown one and the foreign one); the malformed identifier is not looked up.
        Assert.Equal(2, readsBeforeMalformed);
        Assert.Equal(readsBeforeMalformed, _store.FindCalls);
    }

    [Fact]
    public async Task AMalformedIdentifierCausesNoRead()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(GetRequest([Consumer], "upload-xyz"), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _store.FindCalls);
    }

    [Fact]
    public async Task AnUnreachableStoreIsARetryableServiceConditionOnBothOperations()
    {
        var client = Client(new FailingUploadStore(new UploadStoreUnavailableException("57P01", new TimeoutException())));
        var cancellationToken = TestContext.Current.CancellationToken;

        using var created = await client.SendAsync(CreateRequest(), cancellationToken);
        using var read = await client.SendAsync(GetRequest([Consumer], "upload-0123456789abcdef0123456789abcdef"), cancellationToken);
        var createdProblem = await ProblemAsync(created, cancellationToken);
        var readProblem = await ProblemAsync(read, cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, createdProblem.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.ServiceUnavailable, readProblem.GetProperty("code").GetString());
        Assert.DoesNotContain("57P01", createdProblem.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", createdProblem.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(_logs.Lines, line => line.Contains("Upload store unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnexpectedFaultIsAnsweredWithoutTheTextOfTheException()
    {
        var client = Client(new FailingUploadStore(new InvalidOperationException(PlantedSecret)));
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(CreateRequest(), cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);
        var problem = JsonDocument.Parse(answer).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ErrorCodes.ServiceUnavailable, problem.GetProperty("code").GetString());
        Assert.Equal("Service unavailable", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(PlantedSecret, answer, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", answer, StringComparison.Ordinal);
        Assert.Contains(_logs.Lines, line => line.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, line => line.Contains(PlantedSecret, StringComparison.Ordinal));
        Assert.Equal(0, _store.CreateCalls);
    }

    [Fact]
    public async Task EveryProblemCarriesItsContentTypeTraceIdentifierAndNoRequestValue()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(
            CreateRequest(["../<script>"], Json("""{"profile":"header-items-v1","mode":"<script>"}""")),
            cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);
        var problem = JsonDocument.Parse(answer).RootElement;

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.Equal(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
        Assert.DoesNotContain("<script>", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", Headers(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfiguredLifetimeDecidesTheExpiryOfTheSession()
    {
        var client = Client(sessionLifetime: "2.00:00:00");
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(CreateRequest(), cancellationToken);
        using var projection = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("2026-11-03T04:39:23.123Z", projection.RootElement.GetProperty("expiresAt").GetString());
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("7.00:00:01")]
    public void ALifetimeOutOfRangeStopsTheStartNamingTheSettingAndNeverTheValue(string lifetime)
    {
        var error = ConfigurationErrors.Find(Record.Exception(() => Client(sessionLifetime: lifetime)));

        Assert.Equal($"{UploadOptions.SectionPath}:{UploadOptions.SessionLifetimeSetting}", error.Problems[0].Setting);
        Assert.DoesNotContain(lifetime, error.Message, StringComparison.Ordinal);
        Assert.Contains("Uploads:SessionLifetime", error.Message, StringComparison.Ordinal);
    }

    private HttpClient Client(IUploadStore? store = null, TimeProvider? clock = null, string? sessionLifetime = null)
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
            if (sessionLifetime is not null)
            {
                builder.UseSetting($"{UploadOptions.SectionPath}:{UploadOptions.SessionLifetimeSetting}", sessionLifetime);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<IUploadStore>();
                services.AddSingleton(store ?? _store);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock ?? new FixedTimeProvider(Now));
            });
        });
        _disposables.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<string> CreateAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(CreateRequest(), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var projection = JsonDocument.Parse(body);
        return projection.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string? TraceId)> GetAsync(
        HttpClient client,
        string consumerId,
        string uploadId,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(GetRequest([consumerId], uploadId), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        return (response.StatusCode, root, root.TryGetProperty("traceId", out var traceId) ? traceId.GetString() : null);
    }

    // The answer without the trace identifier, in a fixed order: everything else must be identical.
    private static string Normalize(JsonElement problem) => string.Join(
        ';',
        problem.EnumerateObject()
            .Where(member => member.Name != "traceId")
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .Select(member => member.Name + "=" + member.Value.GetRawText()));

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static string Headers(HttpResponseMessage response) =>
        string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));

    private static HttpRequestMessage CreateRequest() => CreateRequest([Consumer], Json(ValidBody));

    private static HttpRequestMessage CreateRequest(string[] identities, HttpContent content)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = content,
        };
        foreach (var identity in identities)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, identity);
        }

        return message;
    }

    private static HttpRequestMessage GetRequest(string[] identities, string uploadId)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
        foreach (var identity in identities)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, identity);
        }

        return message;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType);

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

    /// <summary>A store that fails every call the way an unreachable or faulty database does.</summary>
    private sealed class FailingUploadStore(Exception failure) : IUploadStore
    {
        public Task CreateAsync(UploadRecord record, CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public Task<UploadRecord?> FindAsync(string uploadId, string consumerId, CancellationToken cancellationToken) =>
            Task.FromException<UploadRecord?>(failure);

        public Task<bool> TryStartTransferAsync(
            string uploadId,
            string consumerId,
            UploadContentEncoding encoding,
            CancellationToken cancellationToken) => Task.FromException<bool>(failure);

        public Task<bool> RecordTransferAsync(
            string uploadId,
            UploadContentOutcome outcome,
            long receivedBytes,
            long? stagingSize,
            string? stagingSha256,
            CancellationToken cancellationToken) => Task.FromException<bool>(failure);

        public Task<bool> RejectTransferAsync(
            string uploadId,
            UploadRejectionCode code,
            UploadRejectionReason reason,
            long receivedBytes,
            CancellationToken cancellationToken) => Task.FromException<bool>(failure);

        public Task<bool> ConfirmAsync(
            string uploadId,
            string consumerId,
            string? declaredSha256,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) => Task.FromException<bool>(failure);

        public Task<UploadRecord?> LoadForVerificationAsync(string uploadId, CancellationToken cancellationToken) =>
            Task.FromException<UploadRecord?>(failure);

        public Task<SnapshotCommitResult> CommitReadyAsync(
            SnapshotRecord snapshot,
            CancellationToken cancellationToken) => Task.FromException<SnapshotCommitResult>(failure);

        public Task<bool> CommitRejectedAsync(
            string uploadId,
            UploadRejectionCode code,
            UploadRejectionReason reason,
            CancellationToken cancellationToken) => Task.FromException<bool>(failure);
    }
}
