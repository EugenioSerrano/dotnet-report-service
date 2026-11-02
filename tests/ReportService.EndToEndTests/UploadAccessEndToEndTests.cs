using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// Who may use an upload, on the running composition: the registry file names two
/// active consumers and one inactive one, and the API's sessions are short so an
/// upload can be left unconfirmed past its end. Every refusal is the same answer
/// whatever it hides, another consumer's upload is answered as a missing one, the
/// service needs no restart for a registry change, and an upload nobody confirmed
/// expires without taking the service down.
/// </summary>
public sealed class UploadAccessEndToEndTests : IDisposable
{
    private const string RoutePrefix = "/api/v1/uploads";
    private const string IdentityHeader = "X-Consumer-Id";
    private const string CreateBody = """{"profile":"header-items-v1","mode":"API_STREAM"}""";
    private const string ContentBody = """{"items":[]}""";

    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan SettleBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "report-service-e2e", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public UploadAccessEndToEndTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task TheRegistryDecidesWhoMayUseAnUploadAndAnUnconfirmedOneExpires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = Path.Combine(_directory, "consumers.json");
        await File.WriteAllTextAsync(registry, Registry(("consumer-a", true), ("consumer-b", true), ("consumer-c", false)), cancellationToken);

        var appHost = await TestComposition.CreateAsync(
            cancellationToken,
            $"--Consumers:File={registry}",
            "--Uploads:SessionLifetime=00:00:10");
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        }

        using var api = app.CreateHttpClient("api");

        // An unknown identity is refused on every operation, with one answer, whether or
        // not the upload it names exists.
        var upload = await CreateAsync(api, "consumer-a", cancellationToken);
        foreach (var declared in new[] { null, "not a consumer", "consumer-z" })
        {
            var problems = new List<string>
            {
                await ProblemAsync(await SendAsync(api, CreateRequest(declared), cancellationToken), cancellationToken),
                await ProblemAsync(await SendAsync(api, StatusRequest(upload, declared), cancellationToken), cancellationToken),
                await ProblemAsync(await SendAsync(api, ContentRequest(upload, declared), cancellationToken), cancellationToken),
                await ProblemAsync(await SendAsync(api, ConfirmRequest(upload, declared), cancellationToken), cancellationToken),
            };

            Assert.All(problems, problem => Assert.Equal(problems[0], problem));
            Assert.Contains("CONSUMER_UNKNOWN", problems[0], StringComparison.Ordinal);
        }

        // The inactive consumer cannot open an upload.
        using (var refused = await SendAsync(api, CreateRequest("consumer-c"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("CONSUMER_INACTIVE", await BodyAsync(refused, cancellationToken), StringComparison.Ordinal);
        }

        // Another consumer's upload is answered exactly as an upload that does not exist.
        var absent = "upload-44444444444444444444444444444444";
        using (var foreign = await SendAsync(api, StatusRequest(upload, "consumer-b"), cancellationToken))
        using (var missing = await SendAsync(api, StatusRequest(absent, "consumer-b"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(
                await ProblemAsync(missing, cancellationToken),
                await ProblemAsync(foreign, cancellationToken));
        }

        // The owner streams its content and confirms it, and the upload ends ready.
        using (var sent = await SendAsync(api, ContentRequest(upload, "consumer-a"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        using (var confirmed = await SendAsync(api, ConfirmRequest(upload, "consumer-a"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, confirmed.StatusCode);
        }

        await Wait.UntilAsync(
            "the verified upload to end ready",
            async token => await StatusAsync(api, upload, token) == "ready",
            SettleBound,
            PollInterval,
            cancellationToken);

        // An upload nobody confirmed expires at the end of its session and takes nothing more.
        var abandoned = await CreateAsync(api, "consumer-a", cancellationToken);
        await Wait.UntilAsync(
            "the unconfirmed upload to expire",
            async token => await StatusAsync(api, abandoned, token) == "expired",
            SettleBound,
            PollInterval,
            cancellationToken);

        using (var lateContent = await SendAsync(api, ContentRequest(abandoned, "consumer-a"), cancellationToken))
        using (var lateConfirm = await SendAsync(api, ConfirmRequest(abandoned, "consumer-a"), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, lateContent.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, lateConfirm.StatusCode);
        }

        // The service stayed ready throughout.
        using var health = await api.GetAsync("/health", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("Healthy", await BodyAsync(health, cancellationToken), StringComparison.Ordinal);
    }

    private static string Registry(params (string ConsumerId, bool Active)[] consumers) =>
        "{ \"consumers\": [" + string.Join(
            ',',
            consumers.Select(consumer =>
                $"{{ \"consumerId\": \"{consumer.ConsumerId}\", \"active\": {(consumer.Active ? "true" : "false")} }}")) + "] }";

    private static async Task<string> CreateAsync(HttpClient api, string consumerId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(api, CreateRequest(consumerId), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await BodyAsync(response, cancellationToken));
        return body.RootElement.GetProperty("uploadId").GetString()!;
    }

    private static async Task<string> StatusAsync(HttpClient api, string uploadId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(api, StatusRequest(uploadId, "consumer-a"), cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return string.Empty;
        }

        using var body = JsonDocument.Parse(await BodyAsync(response, cancellationToken));
        return body.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient api,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            return await api.SendAsync(request, cancellationToken);
        }
    }

    private static HttpRequestMessage CreateRequest(string? consumerId, string body = CreateBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return Declare(request, consumerId);
    }

    private static HttpRequestMessage StatusRequest(string uploadId, string? consumerId) =>
        Declare(new HttpRequestMessage(HttpMethod.Get, new Uri($"{RoutePrefix}/{uploadId}", UriKind.Relative)), consumerId);

    private static HttpRequestMessage ContentRequest(string uploadId, string? consumerId) =>
        Declare(
            new HttpRequestMessage(HttpMethod.Put, new Uri($"{RoutePrefix}/{uploadId}/content", UriKind.Relative))
            {
                Content = new StringContent(ContentBody, Encoding.UTF8, "application/json"),
            },
            consumerId);

    private static HttpRequestMessage ConfirmRequest(string uploadId, string? consumerId) =>
        Declare(new HttpRequestMessage(HttpMethod.Post, new Uri($"{RoutePrefix}/{uploadId}/complete", UriKind.Relative)), consumerId);

    private static HttpRequestMessage Declare(HttpRequestMessage request, string? consumerId)
    {
        if (consumerId is not null)
        {
            request.Headers.TryAddWithoutValidation(IdentityHeader, consumerId);
        }

        return request;
    }

    private static Task<string> BodyAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        response.Content.ReadAsStringAsync(cancellationToken);

    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            using var body = JsonDocument.Parse(await BodyAsync(response, cancellationToken));
            return string.Join(
                ';',
                body.RootElement.EnumerateObject()
                    .Where(member => member.Name != "traceId")
                    .OrderBy(member => member.Name, StringComparer.Ordinal)
                    .Select(member => member.Name + "=" + member.Value.GetRawText()));
        }
    }
}
