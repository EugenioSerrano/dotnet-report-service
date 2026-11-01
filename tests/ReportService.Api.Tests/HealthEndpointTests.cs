using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ReportService.Api.Tests;

/// <summary>The API's health endpoint, hosted in memory with a storage root of the test's own.</summary>
public sealed class HealthEndpointTests : IDisposable
{
    private readonly TemporaryDirectory _storage = new();

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task HealthEndpointAnswersHealthyAsPlainText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Storage:Filesystem:Root", _storage.Path));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.True(Directory.Exists(Path.Combine(_storage.Path, "staging", "probe", "api")));
        Assert.Empty(Directory.EnumerateFiles(_storage.Path, "*", SearchOption.AllDirectories));
    }
}
