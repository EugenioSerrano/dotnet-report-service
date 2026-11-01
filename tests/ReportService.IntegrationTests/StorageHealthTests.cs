extern alias orchestration;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using ReportService.ServiceDefaults;
using ReportService.Testing;
using OrchestrationWorker = orchestration::ReportService.Workers.Orchestration.OrchestrationWorkerProgram;

namespace ReportService.IntegrationTests;

/// <summary>
/// The storage health check of the API and of the orchestration worker, each
/// hosted in the test process over a real directory of the test's own: ready
/// while the root is writable, not ready while it is not, ready again once it
/// is restored — with the same process. A provider that does not exist stops
/// the start naming the setting.
/// </summary>
public sealed class StorageHealthTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "report-service-it", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

    public StorageHealthTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ApiIsReadyOnlyWhileItsStorageRootIsWritableAndRecoversWithoutARestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", _root);
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });
        using var client = api.CreateClient();

        await WaitForStatusAsync(client, HttpStatusCode.OK, cancellationToken);
        using (new UnwritableDirectory(_root))
        {
            await WaitForStatusAsync(client, HttpStatusCode.ServiceUnavailable, cancellationToken);
        }

        await WaitForStatusAsync(client, HttpStatusCode.OK, cancellationToken);
    }

    [Fact]
    public async Task OrchestrationWorkerReportsItsStorageCheckAndRecoversWithoutARestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var worker = new WebApplicationFactory<OrchestrationWorker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Temporal:Address", $"127.0.0.1:{ClosedPort()}");
            builder.UseSetting("Temporal:Namespace", "default");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:Filesystem:Root", _root);
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });
        using var client = worker.CreateClient();

        var healthy = await WaitForStorageCheckAsync(client, "Healthy", cancellationToken);
        Assert.Equal("filesystem", healthy.GetProperty("provider").GetString());
        Assert.True(healthy.GetProperty("writable").GetBoolean());
        Assert.True(healthy.GetProperty("readable").GetBoolean());

        using (new UnwritableDirectory(_root))
        {
            var failed = await WaitForStorageCheckAsync(client, "Unhealthy", cancellationToken);
            Assert.StartsWith("storage.", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(_root, failed.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        await WaitForStorageCheckAsync(client, "Healthy", cancellationToken);
    }

    [Fact]
    public async Task UnknownProviderStopsTheStartNamingTheSetting()
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Provider", "object-store-that-does-not-exist");
            builder.UseSetting("Storage:Filesystem:Root", _root);
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });

        var exception = Record.Exception(() => api.CreateClient());

        var error = Find(exception);
        Assert.Equal("Storage:Provider", error.Setting);
        Assert.DoesNotContain("object-store-that-does-not-exist", error.Message, StringComparison.Ordinal);
    }

    private static Task WaitForStatusAsync(HttpClient client, HttpStatusCode expected, CancellationToken cancellationToken) =>
        Wait.UntilAsync(
            $"/health answering {(int)expected}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                return response.StatusCode == expected;
            },
            Deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);

    // Polls the worker's health body until its storage check has the expected status; returns the check's data.
    private static async Task<JsonElement> WaitForStorageCheckAsync(HttpClient client, string status, CancellationToken cancellationToken)
    {
        JsonElement data = default;
        await Wait.UntilAsync(
            $"the storage check reporting {status}",
            async token =>
            {
                using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), token);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var check = body.RootElement.GetProperty("checks").EnumerateArray()
                    .Single(c => c.GetProperty("name").GetString() == StorageHealthCheck.Name);
                data = check.GetProperty("data").Clone();
                return check.GetProperty("status").GetString() == status;
            },
            Deadline,
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        return data;
    }

    private static ConfigurationError Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConfigurationError error)
            {
                return error;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return Find(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No configuration error was raised; got {exception?.GetType().FullName ?? "no exception"}.");
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
