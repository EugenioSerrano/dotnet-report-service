using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
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

namespace ReportService.Api.Tests.Uploads;

/// <summary>
/// The API on its real pipeline, with the upload records kept in memory, the clock in
/// the test's hand and the consumer registry in a file the test rewrites: everything
/// the upload operations decide — who may act, when an upload ends — can then be
/// asserted exactly, and a registry change can be made to land while the service runs.
/// </summary>
internal sealed class UploadApiHost : IDisposable
{
    /// <summary>The instant the service starts at, a day before any upload's end.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 4, 39, 23, 123, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _registryPath;

    /// <summary>Starts the service over a registry carrying <paramref name="consumers"/>.</summary>
    /// <param name="consumers">The registry, as identity and active flag, in file order.</param>
    /// <param name="sessionLifetime">How long a new session accepts data, as the configuration writes it.</param>
    public UploadApiHost((string ConsumerId, bool Active)[] consumers, string sessionLifetime = "1.00:00:00")
    {
        Clock = new ManualTimeProvider(Now);
        Lifetime = TimeSpan.Parse(sessionLifetime, System.Globalization.CultureInfo.InvariantCulture);
        _registryPath = _directory.Write("consumers.json", Registry(consumers));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, _registryPath);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.UseSetting($"{UploadOptions.SectionPath}:{UploadOptions.SessionLifetimeSetting}", sessionLifetime);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<IUploadStore>();
                services.AddSingleton<IUploadStore>(Store);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        });
        Client = _factory.CreateClient();
    }

    /// <summary>How long a new session of this host accepts data.</summary>
    public TimeSpan Lifetime { get; }

    /// <summary>The upload records the service writes.</summary>
    public InMemoryUploadStore Store { get; } = new();

    /// <summary>The clock the service reads.</summary>
    public ManualTimeProvider Clock { get; }

    /// <summary>Every line the service logged.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>The client the cases send with.</summary>
    public HttpClient Client { get; }

    /// <summary>The object storage the service writes to.</summary>
    public IObjectStore Objects => _factory.Services.GetRequiredService<IObjectStore>();

    /// <summary>Rewrites the registry file, as an operator would while the service runs.</summary>
    /// <param name="consumers">The registry, as identity and active flag, in file order.</param>
    public void ReplaceRegistry(params (string ConsumerId, bool Active)[] consumers) =>
        _directory.Write("consumers.json", Registry(consumers));

    /// <summary>Opens an upload for <paramref name="consumerId"/> and returns its identifier.</summary>
    /// <param name="consumerId">The declared consumer.</param>
    public async Task<string> CreateAsync(string consumerId = "dev-consumer")
    {
        using var response = await Client.SendAsync(CreateRequest(consumerId), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("uploadId").GetString()!;
    }

    /// <summary>The request that opens an upload, declared as <paramref name="consumerId"/>.</summary>
    public static HttpRequestMessage CreateRequest(string? consumerId, string body = """{"profile":"header-items-v1","mode":"API_STREAM"}""")
    {
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(UploadEndpoints.RoutePrefix, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
        };
        Declare(message, consumerId);
        return message;
    }

    /// <summary>The request that reads an upload's status, declared as <paramref name="consumerId"/>.</summary>
    public static HttpRequestMessage StatusRequest(string uploadId, string? consumerId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}", UriKind.Relative));
        Declare(message, consumerId);
        return message;
    }

    /// <summary>The request that sends content, declared as <paramref name="consumerId"/>.</summary>
    public static HttpRequestMessage ContentRequest(string uploadId, string? consumerId, string body = """{"items":[]}""")
    {
        var message = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/content", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, CreateUploadRequestReader.JsonMediaType),
        };
        Declare(message, consumerId);
        return message;
    }

    /// <summary>The request that confirms an upload, declared as <paramref name="consumerId"/>.</summary>
    public static HttpRequestMessage ConfirmRequest(string uploadId, string? consumerId, string? body = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"{UploadEndpoints.RoutePrefix}/{uploadId}/complete", UriKind.Relative));
        if (body is not null)
        {
            message.Content = new StringContent(body, Encoding.UTF8, CompleteUploadRequestReader.JsonMediaType);
        }

        Declare(message, consumerId);
        return message;
    }

    /// <summary>The problem document of <paramref name="response"/>, without its trace identifier.</summary>
    /// <param name="response">The answer.</param>
    public static async Task<string> ProblemAsync(HttpResponseMessage response)
    {
        using var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return string.Join(
            ';',
            body.RootElement.EnumerateObject()
                .Where(member => member.Name != "traceId")
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => member.Name + "=" + member.Value.GetRawText()));
    }

    /// <summary>The status an upload's projection answers.</summary>
    /// <param name="response">The answer.</param>
    public static async Task<string> StatusOfAsync(HttpResponseMessage response)
    {
        using var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>Waits until <paramref name="probe"/> holds, so a regrouped registry is observed without a fixed sleep.</summary>
    /// <param name="what">What is awaited.</param>
    /// <param name="probe">The probe.</param>
    public static Task UntilAsync(string what, Func<CancellationToken, Task<bool>> probe) =>
        Wait.UntilAsync(what, probe, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(50), CancellationToken.None);

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _directory.Dispose();
    }

    private static void Declare(HttpRequestMessage message, string? consumerId)
    {
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }
    }

    private static string Registry(IEnumerable<(string ConsumerId, bool Active)> consumers) =>
        "{ \"consumers\": [" + string.Join(
            ',',
            consumers.Select(consumer =>
                $"{{ \"consumerId\": \"{consumer.ConsumerId}\", \"active\": {(consumer.Active ? "true" : "false")} }}")) + "] }";
}
