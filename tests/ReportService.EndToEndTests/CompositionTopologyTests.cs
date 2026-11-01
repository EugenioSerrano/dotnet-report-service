using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The local composition runs the three processes: the API, the orchestration
/// worker and the PDF worker. Each worker is ready against the workflow engine
/// with its declared queues and no registered work; the PDF worker still
/// reports its browser check.
/// </summary>
public sealed class CompositionTopologyTests
{
    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);

    private static readonly string[] Resources = ["postgres", "cache", "temporal", "render-worker", "orchestration-worker", "api"];

    [Fact]
    public async Task ThreeProcessesRunAndEachWorkerIsReadyAgainstTheEngine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ReportService_AppHost>(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            foreach (var resource in Resources)
            {
                await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, deadline.Token);
            }
        }

        using var api = app.CreateHttpClient("api");
        Assert.Equal("Healthy", await api.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken));
        using (var registry = JsonDocument.Parse(await api.GetStringAsync(new Uri("/health/registry", UriKind.Relative), cancellationToken)))
        {
            var data = registry.RootElement.GetProperty("checks")[0].GetProperty("data");
            Assert.Equal(2, data.GetProperty("entries").GetInt32());
        }

        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using (var health = JsonDocument.Parse(await orchestration.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());
            var queues = engine.GetProperty("data").GetProperty("queues");
            Assert.Equal("no registered work", queues.GetProperty("report-orchestration").GetString());
            Assert.Equal("no registered work", queues.GetProperty("report-data").GetString());
        }

        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        using (var health = JsonDocument.Parse(await renderWorker.GetStringAsync(new Uri("/health", UriKind.Relative), cancellationToken)))
        {
            Assert.Equal("Healthy", Check(health, "chromium").GetProperty("status").GetString());
            var engine = Check(health, "workflow-engine");
            Assert.Equal("Healthy", engine.GetProperty("status").GetString());
            Assert.Equal("no registered work", engine.GetProperty("data").GetProperty("queues").GetProperty("report-pdf-rendering").GetString());
        }
    }

    private static JsonElement Check(JsonDocument health, string name) =>
        Assert.Single(health.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == name);
}
