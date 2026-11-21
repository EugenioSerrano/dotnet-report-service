using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.Api.Tests;

/// <summary>
/// The API's health endpoint, hosted in memory with a storage root of the test's
/// own: the JSON body of the processes, with the metadata-database check reported
/// and its status carried in the response code.
/// </summary>
public sealed class HealthEndpointTests : IDisposable
{
    private const string Baseline = "0001_baseline.sql";
    private readonly TemporaryDirectory _storage = new();

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task HealthEndpointAnswersJsonWithTheMetadataDatabaseCheck()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(json);
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        var check = FindCheck(body.RootElement, MetadataDatabaseStub.CheckName);
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        var data = check.GetProperty("data");
        Assert.Equal(Baseline, data.GetProperty("expected").GetString());
        Assert.Equal(Baseline, data.GetProperty("applied").GetString());
        Assert.True(Directory.Exists(Path.Combine(_storage.Path, "staging", "probe", "api")));
        Assert.Empty(Directory.EnumerateFiles(_storage.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnUnhealthyMetadataDatabaseAnswersNotReady()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = Factory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "Unhealthy",
            FindCheck(body.RootElement, MetadataDatabaseStub.CheckName).GetProperty("status").GetString());
    }

    [Fact]
    public void TheApiStopsAtStartWhenTheMetadataConnectionStringIsMissing()
    {
        using var probe = StartFailureProbe.Create(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Storage:Filesystem:Root"] = _storage.Path,
                [DatabaseOptions.ExpectedSchemaVersionSetting] = Baseline,
            },
            replacements: services => MetadataDatabaseStub.UseHealthy(services));

        var error = probe.Refusal();

        Assert.Equal("setting 'ConnectionStrings:reportservice': is required", error.Message);
    }

    [Fact]
    public void TheApiStopsAtStartWhenTheExpectedSchemaVersionIsMissing()
    {
        using var probe = StartFailureProbe.Create(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Storage:Filesystem:Root"] = _storage.Path,
                [DatabaseOptions.ConnectionStringSetting] = MetadataDatabaseStub.ConnectionString,
            },
            replacements: services => MetadataDatabaseStub.UseHealthy(services));

        var error = probe.Refusal();

        Assert.Equal("setting 'Database:ExpectedSchemaVersion': is required", error.Message);
    }

    private WebApplicationFactory<Program> Factory(HealthStatus status = HealthStatus.Healthy) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Filesystem:Root", _storage.Path);
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, Baseline);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services, status));
        });

    private static JsonElement FindCheck(JsonElement root, string name) =>
        root.GetProperty("checks").EnumerateArray().Single(check => check.GetProperty("name").GetString() == name);
}
