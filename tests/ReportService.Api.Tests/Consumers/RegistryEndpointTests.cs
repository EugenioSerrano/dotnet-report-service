using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ReportService.Api.Consumers;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Consumers;

/// <summary>The API hosted in memory with a registry file of the test's own.</summary>
public sealed class RegistryEndpointTests : IDisposable
{
    private const string PlantedSecret = "planted-secret-ee9142";
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task RegistryEndpointReportsCountAndFingerprintOnly()
    {
        var path = _directory.Write("consumers.json", """
            { "consumers": [ { "consumerId": "only-one", "displayName": "Only", "active": true } ] }
            """);
        await using var factory = Factory(path);
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.GetAsync(new Uri("/health/registry", UriKind.Relative), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("only-one", json, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(json);
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("consumer-registry", check.GetProperty("name").GetString());
        var data = check.GetProperty("data");
        Assert.Equal(1, data.GetProperty("entries").GetInt32());
        Assert.Equal(
            ConsumerRegistrySnapshot.ComputeFingerprint([new ConsumerEntry("only-one", null, true, null, null)]),
            data.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task HealthKeepsItsPlainShapeWithAnEngineAddressThatAnswersNothing()
    {
        var path = _directory.Write("consumers.json", """{ "consumers": [] }""");
        await using var factory = Factory(path, ("Temporal:Address", "127.0.0.1:1"));
        using var client = factory.CreateClient();
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public async Task InvalidInitialRegistryStopsStartNamingTheEntryWithoutValues()
    {
        var path = _directory.Write("consumers.json", $$"""
            { "consumers": [
                { "consumerId": "a", "active": true },
                { "consumerId": "a", "active": true, "secret": "{{PlantedSecret}}" } ] }
            """);
        await using var factory = Factory(path);

        var error = FindConfigurationError(Record.Exception(() => factory.CreateClient()));

        Assert.Equal("setting 'consumers[1]': consumer 'a' duplicates consumers[0]", error.Message);
        Assert.DoesNotContain(PlantedSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRegistryFileStopsStartNamingTheSettingNotThePath()
    {
        var path = Path.Combine(_directory.Path, "absent", "consumers.json");
        await using var factory = Factory(path);

        var error = FindConfigurationError(Record.Exception(() => factory.CreateClient()));

        Assert.Equal("setting 'Consumers:File': the registry file does not exist", error.Message);
        Assert.DoesNotContain("absent", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryFileThatIsNotJsonStopsStart()
    {
        var path = _directory.Write("consumers.json", "{ not json");
        await using var factory = Factory(path);

        var error = FindConfigurationError(Record.Exception(() => factory.CreateClient()));

        Assert.Equal("consumers", error.Setting);
    }

    private static WebApplicationFactory<Program> Factory(string registryPath, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Consumers:File", registryPath);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    private static ConfigurationError FindConfigurationError(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return FindConfigurationError(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No configuration error was raised; got {exception?.GetType().FullName ?? "no exception"}.");
    }
}
