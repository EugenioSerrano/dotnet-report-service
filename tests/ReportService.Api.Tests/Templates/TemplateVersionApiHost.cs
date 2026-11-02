using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReportService.Api.Consumers;
using ReportService.Api.Http;
using ReportService.Api.Templates;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Records;
using ReportService.Testing;

namespace ReportService.Api.Tests.Templates;

/// <summary>
/// The API on its real pipeline, with the taken versions kept in memory and the
/// consumer registry in a file the test writes: who may read a version, what the
/// answer carries and whether the read reached the store at all can then be
/// asserted exactly, without a database.
/// </summary>
internal sealed class TemplateVersionApiHost : IDisposable
{
    /// <summary>The instant every record this host holds was taken at, with ticks below the millisecond.</summary>
    public static readonly DateTimeOffset TakenAt =
        new DateTimeOffset(2026, 11, 1, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);

    /// <summary>The consumer that owns the version every case reads.</summary>
    public const string Owner = "billing-system";

    /// <summary>Another registered consumer, which owns nothing.</summary>
    public const string Other = "dev-consumer";

    /// <summary>A registered consumer that was made inactive.</summary>
    public const string Inactive = "consumer-c";

    private readonly TemporaryDirectory _directory = new();
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>Starts the service over a registry carrying the three consumers, with <paramref name="store"/>.</summary>
    /// <param name="store">The store the service reads; the host's own in-memory store when omitted.</param>
    public TemplateVersionApiHost(ITemplateVersionStore? store = null)
    {
        var registryPath = _directory.Write(
            "consumers.json",
            "{ \"consumers\": ["
            + $"{{ \"consumerId\": \"{Owner}\", \"active\": true }}, "
            + $"{{ \"consumerId\": \"{Other}\", \"active\": true }}, "
            + $"{{ \"consumerId\": \"{Inactive}\", \"active\": false }}"
            + "] }");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(ConsumerRegistryHosting.FileSetting, registryPath);
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_directory.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                MetadataDatabaseStub.UseHealthy(services);
                services.RemoveAll<ITemplateVersionStore>();
                services.AddSingleton(store ?? Store);
            });
        });
        Client = _factory.CreateClient();
    }

    /// <summary>The versions the service reads.</summary>
    public InMemoryTemplateVersionStore Store { get; } = new();

    /// <summary>Every line the service logged.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>The client the cases send with.</summary>
    public HttpClient Client { get; }

    /// <summary>The services the application composed, for the cases that assert what it does not register.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>Holds the pilot version, owned by <see cref="Owner"/>, in the host's store.</summary>
    /// <param name="takenAt">When the version was taken; the host's instant when omitted.</param>
    /// <param name="owner">The consumer that owns it.</param>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    public TemplateVersionRecord Seed(
        DateTimeOffset? takenAt = null,
        string owner = Owner,
        string templateId = "invoice-energy",
        string version = "1.1.0")
    {
        var record = TemplateVersionRecords.Sample(
            templateId: templateId,
            version: version,
            owner: owner,
            takenAt: takenAt ?? TakenAt);
        Store.Seed(record);
        return record;
    }

    /// <summary>The request that reads a version, declared as <paramref name="consumerId"/>.</summary>
    /// <param name="templateId">The template's identifier.</param>
    /// <param name="version">The version.</param>
    /// <param name="consumerId">The declared consumer; null sends no identity at all.</param>
    public static HttpRequestMessage ReadRequest(string templateId, string version, string? consumerId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{GetTemplateVersionEndpoint.RoutePrefix}/{templateId}/versions/{version}", UriKind.Relative));
        if (consumerId is not null)
        {
            message.Headers.TryAddWithoutValidation(ConsumerIdentity.HeaderName, consumerId);
        }

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

    /// <inheritdoc />
    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _directory.Dispose();
        Logs.Dispose();
    }
}
