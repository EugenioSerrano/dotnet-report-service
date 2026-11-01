using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// Where telemetry is exported is configuration: with no destination set, the
/// three processes start healthy, report no export error, and still record the
/// metric points the end-to-end level reads — the development capture is not a
/// destination.
/// </summary>
public sealed class NoDestinationCompositionTests
{
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan EvidenceDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task TheThreeProcessesStartHealthyWithNoDestinationAndStillRecordTheirMetrics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        ClearDestinations(appHost);

        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(HealthyDeadline);
            foreach (var resource in new[] { "render-worker", "orchestration-worker", "api" })
            {
                await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, deadline.Token);
            }
        }

        using var api = app.CreateHttpClient("api");
        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        foreach (var (service, client) in new[]
        {
            (ServiceNames.Api, api),
            (ServiceNames.OrchestrationWorker, orchestration),
            (ServiceNames.RenderWorker, renderWorker),
        })
        {
            await Wait.UntilAsync(
                $"metric points from {service} without a destination",
                async token =>
                {
                    var capture = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                        new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative),
                        token);
                    return capture.GetProperty("service").GetProperty("name").GetString() == service
                        && capture.GetProperty("points").GetArrayLength() > 0;
                },
                EvidenceDeadline,
                PollInterval,
                cancellationToken);
        }
    }

    private static void ClearDestinations(IDistributedApplicationTestingBuilder builder)
    {
        foreach (var name in new[] { "api", "orchestration-worker", "render-worker" })
        {
            var resource = builder.Resources.Single(candidate => candidate.Name == name);
            switch (resource)
            {
                case ContainerResource container:
                    builder.CreateResourceBuilder(container).WithEnvironment(TestComposition.OtlpEndpointVariable, string.Empty);
                    break;
                case ProjectResource project:
                    builder.CreateResourceBuilder(project).WithEnvironment(TestComposition.OtlpEndpointVariable, string.Empty);
                    break;
                default:
                    throw new InvalidOperationException($"The composition's resource {name} is neither a container nor a project.");
            }
        }
    }
}
