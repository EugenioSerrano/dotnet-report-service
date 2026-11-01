using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using ReportService.ServiceDefaults;
using ReportService.Testing;

namespace ReportService.IntegrationTests;

/// <summary>The API starts and is ready with no workflow engine reachable at all.</summary>
public sealed class ApiWithoutEngineTests
{
    [Fact]
    public async Task ApiIsHealthyWithAnEngineAddressWhereNothingListens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{ClosedPort()}");
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
    }

    // A port that was free a moment ago and has nothing listening on it now.
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
