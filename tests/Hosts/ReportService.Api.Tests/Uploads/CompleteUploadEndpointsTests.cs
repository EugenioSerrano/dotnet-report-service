using System.Diagnostics;
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
using ReportService.ServiceDefaults;
using ReportService.Testing;
using ReportService.Uploads;
using Xunit;

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The confirmation through the API's real pipeline: it records the request that
/// starts the verification and answers before any of that work runs; a repeated
/// confirmation answers the current state, and only the first one moves the upload.
/// </summary>
public sealed class CompleteUploadEndpointsTests : IDisposable
{
    private const string Consumer = "dev-consumer";
    private const string OtherConsumer = "other-consumer";
    private const string Hash = "9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3";

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
    public async Task ConfirmingAReceivedBodyAnswersAcceptedAndRecordsOneRequest()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);

        using var response = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken);
        var projection = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(UploadEndpoints.RoutePrefix + "/" + uploadId, response.Headers.Location?.OriginalString);
        Assert.Equal("verifying", projection.GetProperty("status").GetString());
        Assert.Equal("2026-11-01T04:39:23.123Z", projection.GetProperty("completedAt").GetString());
        Assert.Single(_store.Intents);
        Assert.Contains(_logs.Lines, line => line.Contains("verification requested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeclaredHashIsRecordedInLowerCase()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);

        using var response = await client.SendAsync(
            ConfirmRequest([Consumer], uploadId, $$"""{"sha256":"{{Hash.ToUpperInvariant()}}"}"""),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(Hash, Assert.Single(_store.Records).DeclaredSha256);
    }

    [Fact]
    public async Task ConfirmingAnUploadWithoutContentIsRefused()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);

        using var response = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.UploadStateConflict, problem.GetProperty("code").GetString());
        Assert.Equal(CompleteUploadEndpoint.NoContentDetail, problem.GetProperty("detail").GetString());
        Assert.Empty(_store.Intents);
    }

    [Fact]
    public async Task ARepeatedConfirmationAnswersTheCurrentStateAndRecordsNoSecondRequest()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);
        using (var first = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var repeated = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Single(_store.Intents);

        await _store.CommitReadyAsync(
            new SnapshotRecord(
                uploadId,
                Consumer,
                ReportService.Storage.ObjectKey.FromSegments("snapshots", uploadId, "attempt"),
                UploadContentEncoding.None,
                DataProfile.HeaderItemsV1,
                Hash,
                2,
                Hash,
                2,
                Now),
            Now,
            cancellationToken);

        using var afterEnd = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken);
        var projection = await JsonAsync(afterEnd, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, afterEnd.StatusCode);
        Assert.Equal("ready", projection.GetProperty("status").GetString());
        Assert.Single(_store.Intents);
    }

    [Fact]
    public async Task ARepeatedConfirmationDeclaringAnotherHashIsRefused()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);
        using (var first = await client.SendAsync(
                   ConfirmRequest([Consumer], uploadId, $$"""{"sha256":"{{Hash}}"}"""),
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var repeated = await client.SendAsync(
            ConfirmRequest([Consumer], uploadId, $$"""{"sha256":"{{new string('b', 64)}}"}"""),
            cancellationToken);
        var problem = await JsonAsync(repeated, cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Equal(CompleteUploadEndpoint.DifferentDeclarationDetail, problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnInterruptedTransferCanBeConfirmed()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateAsync(client, cancellationToken);
        await RecordInterruptedAsync(uploadId, cancellationToken);

        using var response = await client.SendAsync(ConfirmRequest([Consumer], uploadId, null), cancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_store.Intents);
    }

    [Theory]
    [InlineData("""{"sha256":"abc"}""", "invalid-format", "sha256", "The field 'sha256' must be 64 hexadecimal characters.")]
    [InlineData("""{"sha256":null}""", "null-field", "sha256", "The field 'sha256' must not be null.")]
    [InlineData("""{"sha256":1}""", "wrong-type", "sha256", "The field 'sha256' must be a string.")]
    [InlineData("""{"sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3","sha256":"9ffcbf0acedfe4529a6093c4c669efab29c2d523a7794bacfb8eb8a946ca8dd3"}""", "duplicate-field", "sha256", "The field 'sha256' appears more than once.")]
    [InlineData("""{"planted":true}""", "unknown-field", null, "The request contains 1 field that is not part of this operation.")]
    [InlineData("[]", "malformed-json", null, "The request body must be a single JSON object.")]
    public async Task AMalformedConfirmationIsRefusedBeforeAnythingChanges(
        string body,
        string reason,
        string? field,
        string detail)
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);

        using var response = await client.SendAsync(ConfirmRequest([Consumer], uploadId, body), cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(reason, problem.GetProperty("reason").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
        Assert.Equal(UploadStatus.Uploading, Assert.Single(_store.Records).Status);
        Assert.Empty(_store.Intents);
    }

    [Fact]
    public async Task AConfirmationBodyOverTheBoundIsRefused()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);
        var body = new string(' ', CompleteUploadRequestReader.MaxBodyBytes + 1);

        using var response = await client.SendAsync(ConfirmRequest([Consumer], uploadId, body), cancellationToken);

        Assert.Equal(413, (int)response.StatusCode);
        Assert.Empty(_store.Intents);
    }

    [Fact]
    public async Task AForeignUploadIsAnsweredExactlyAsAnAbsentOne()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);

        using var foreign = await client.SendAsync(ConfirmRequest([OtherConsumer], uploadId, null), cancellationToken);
        using var unknown = await client.SendAsync(ConfirmRequest([Consumer], "upload-44444444444444444444444444444444", null), cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(ErrorCodes.InputNotFound, (await JsonAsync(foreign, cancellationToken)).GetProperty("code").GetString());
        Assert.Empty(_store.Intents);
    }

    [Fact]
    public async Task NoIdentityIsRefusedBeforeTheBodyIsRead()
    {
        var client = Client();
        var cancellationToken = TestContext.Current.CancellationToken;
        var uploadId = await CreateWithBodyAsync(client, cancellationToken);

        using var response = await client.SendAsync(ConfirmRequest([], uploadId, """{"sha256":"abc"}"""), cancellationToken);
        var problem = await JsonAsync(response, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ConsumerUnknown, problem.GetProperty("code").GetString());
    }

    private HttpClient Client()
    {
        var registry = _directory.Write(
            "consumers.json",
            """{ "consumers": [ { "consumerId": "dev-consumer", "active": true }, { "consumerId": "other-consumer", "active": true } ] }""");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registry);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<IUploadStore>();
                services.AddSingleton<IUploadStore>(_store);
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
            new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
            {
                Content = new StringContent("""{"profile":"header-items-v1","mode":"API_STREAM"}""", Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var projection = JsonDocument.Parse(body);
        return projection.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task<string> CreateWithBodyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var uploadId = await CreateAsync(client, cancellationToken);
        var body = Encoding.UTF8.GetBytes("""{"items":[]}""");
        using var sent = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(CreateUploadRequestReader.JsonMediaType) },
                },
                Headers = { { ConsumerIdentity.HeaderName, Consumer } },
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return uploadId;
    }

    private async Task RecordInterruptedAsync(string uploadId, CancellationToken cancellationToken)
    {
        Assert.True(await _store.TryStartTransferAsync(uploadId, Consumer, UploadContentEncoding.None, Now, cancellationToken));
        Assert.True(await _store.RecordTransferAsync(uploadId, UploadContentOutcome.Interrupted, 10, null, null, Now, cancellationToken));
    }

    private static HttpRequestMessage ConfirmRequest(string[] identities, string uploadId, string? body)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative));
        if (body is not null)
        {
            message.Content = new StringContent(body, Encoding.UTF8, CompleteUploadRequestReader.JsonMediaType);
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
}
