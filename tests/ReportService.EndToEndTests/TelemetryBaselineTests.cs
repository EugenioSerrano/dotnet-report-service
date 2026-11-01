using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Testing;

namespace ReportService.EndToEndTests;

/// <summary>
/// The observability baseline on the running composition: each process's metric
/// points read from its own development-only capture, its logs, spans and traces
/// read from the run's telemetry view, the version every process reports, a trace
/// that crosses a process boundary, and a job identifier that appears on logs and
/// spans and on no metric.
/// </summary>
public sealed class TelemetryBaselineTests
{
    // The first run may build the PDF base image and publish the render worker image.
    private static readonly TimeSpan HealthyDeadline = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan EvidenceDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] Services =
        [ServiceNames.Api, ServiceNames.OrchestrationWorker, ServiceNames.RenderWorker];

    [Fact]
    public async Task EachProcessReportsItsOwnMetricsLogsSpansAndTracesWithTheBuildVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = ProductVersion.Informational;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await WaitForHealthyAsync(app, cancellationToken);

        using var api = app.CreateHttpClient("api");
        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        using var dashboard = new HttpClient { BaseAddress = await app.GetDashboardUrlAsync(cancellationToken) };

        // Metrics: read from each process's own capture, which carries the resource the
        // process declares — under its own service name and the build's version.
        foreach (var (service, client) in new[]
        {
            (ServiceNames.Api, api),
            (ServiceNames.OrchestrationWorker, orchestration),
            (ServiceNames.RenderWorker, renderWorker),
        })
        {
            await Wait.UntilAsync(
                $"metric points from {service}",
                async token =>
                {
                    var capture = await CaptureAsync(client, token);
                    return capture.GetProperty("service").GetProperty("name").GetString() == service
                        && capture.GetProperty("service").GetProperty("version").GetString() == version
                        && capture.GetProperty("points").GetArrayLength() > 0;
                },
                EvidenceDeadline,
                PollInterval,
                cancellationToken);
        }

        // Logs, spans and traces: read from the run's telemetry view, which is the wire the
        // same exporter registration carries.
        foreach (var service in Services)
        {
            await Wait.UntilAsync(
                $"logs and spans from {service} in the telemetry view",
                async token => HasService(await DashboardAsync(dashboard, "logs", token), "resourceLogs", service)
                    && HasService(await DashboardAsync(dashboard, "spans", token), "resourceSpans", service),
                EvidenceDeadline,
                PollInterval,
                cancellationToken);
        }
    }

    [Fact]
    public async Task EveryStartUpLineNamesTheVersionItsCommitAndTheRuntimeAndTheWorkerItsBrowser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var version = ProductVersion.Informational;
        var commit = ProductVersion.CommitOf(version);
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await WaitForHealthyAsync(app, cancellationToken);
        using var dashboard = new HttpClient { BaseAddress = await app.GetDashboardUrlAsync(cancellationToken) };

        foreach (var service in Services)
        {
            await Wait.UntilAsync(
                $"the start-up line of {service}",
                async token => Lines(await DashboardAsync(dashboard, "logs", token)).Any(line =>
                    line.Service == service
                    && line.Message.Contains("version=" + version, StringComparison.Ordinal)
                    && line.Message.Contains("commit=" + commit, StringComparison.Ordinal)
                    && line.Message.Contains("runtime=", StringComparison.Ordinal)),
                EvidenceDeadline,
                PollInterval,
                cancellationToken);
        }

        await Wait.UntilAsync(
            "the browser line of the PDF worker",
            async token => Lines(await DashboardAsync(dashboard, "logs", token)).Any(line =>
                line.Service == ServiceNames.RenderWorker
                && line.Message.Contains("Chromium 153.0.8010.12", StringComparison.Ordinal)),
            EvidenceDeadline,
            PollInterval,
            cancellationToken);
    }

    [Fact]
    public async Task ARequestTracedAtTheApiCrossesIntoTheOrchestrationWorkerInOneTrace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await WaitForHealthyAsync(app, cancellationToken);
        using var api = app.CreateHttpClient("api");
        using var dashboard = new HttpClient { BaseAddress = await app.GetDashboardUrlAsync(cancellationToken) };

        var propagated = await api.GetFromJsonAsync<JsonElement>(
            new Uri(TelemetryDiagnostics.PropagateRoute, UriKind.Relative),
            cancellationToken);
        Assert.True(propagated.GetProperty("same").GetBoolean(), $"The API and the worker answered with different traces: {propagated}");
        var traceId = propagated.GetProperty("traceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(traceId));

        await Wait.UntilAsync(
            $"one trace {traceId} holding both services",
            async token =>
            {
                var services = ServicesOfTrace(await DashboardAsync(dashboard, "spans", token), traceId!);
                return services.Contains(ServiceNames.Api) && services.Contains(ServiceNames.OrchestrationWorker);
            },
            EvidenceDeadline,
            PollInterval,
            cancellationToken);
    }

    [Fact]
    public async Task ASyntheticJobIdentifierIsOnEveryLogEntryAndSpanOfTheUnitAndOnNoMetricPoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var jobId = "synthetic-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var appHost = await TestComposition.CreateAsync(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await WaitForHealthyAsync(app, cancellationToken);

        using var api = app.CreateHttpClient("api");
        using var orchestration = app.CreateHttpClient("orchestration-worker");
        using var renderWorker = app.CreateHttpClient("render-worker", "http");
        using var dashboard = new HttpClient { BaseAddress = await app.GetDashboardUrlAsync(cancellationToken) };

        foreach (var client in new[] { api, renderWorker })
        {
            using var response = await client.PostAsJsonAsync(
                new Uri(TelemetryDiagnostics.UnitOfWorkRoute, UriKind.Relative),
                new { jobId },
                cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await Wait.UntilAsync(
            $"the unit of work {jobId} in the logs and the spans",
            async token => LogRecords(await DashboardAsync(dashboard, "logs", token)).Count(unit => CarriesJob(unit.Record, jobId)) >= 2
                && SpansOf(await DashboardAsync(dashboard, "spans", token)).Count(span => CarriesJob(span, jobId)) >= 2,
            EvidenceDeadline,
            PollInterval,
            cancellationToken);

        // The identifier never becomes a metric dimension: no captured point of any of the
        // three processes carries a tag whose name contains "job".
        foreach (var client in new[] { api, orchestration, renderWorker })
        {
            var capture = await CaptureAsync(client, cancellationToken);
            Assert.DoesNotContain(
                capture.GetProperty("points").EnumerateArray().SelectMany(point => point.GetProperty("tags").EnumerateObject()),
                tag => tag.Name.Contains("job", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task WaitForHealthyAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthyDeadline);
        foreach (var resource in new[] { "temporal", "render-worker", "orchestration-worker", "api" })
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, deadline.Token);
        }
    }

    private static async Task<JsonElement> CaptureAsync(HttpClient client, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<JsonElement>(new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative), cancellationToken);

    private static async Task<JsonElement> DashboardAsync(HttpClient dashboard, string signal, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await dashboard.GetStringAsync(new Uri("/api/telemetry/" + signal, UriKind.Relative), cancellationToken));
        return document.RootElement.Clone();
    }

    private static bool HasService(JsonElement root, string collection, string service) =>
        root.TryGetProperty(collection, out var resources)
        && resources.EnumerateArray().Any(resource => ServiceOf(resource) == service);

    private static string? ServiceOf(JsonElement resource)
    {
        if (!resource.TryGetProperty("resource", out var descriptor)
            || !descriptor.TryGetProperty("attributes", out var attributes))
        {
            return null;
        }

        return attributes.EnumerateArray()
            .Where(attribute => attribute.GetProperty("key").GetString() == "service.name")
            .Select(attribute => attribute.GetProperty("value").GetProperty("stringValue").GetString())
            .FirstOrDefault();
    }

    private static IEnumerable<(string Service, string Message)> Lines(JsonElement logs) =>
        LogRecords(logs)
            .Select(record => (record.Service, Message: record.Message ?? string.Empty))
            .Where(line => line.Message.Contains("started", StringComparison.Ordinal)
                || line.Message.Contains("Chromium", StringComparison.Ordinal));

    private static IEnumerable<(string Service, string? Message, JsonElement Record)> LogRecords(JsonElement logs)
    {
        List<(string, string?, JsonElement)> records = [];
        if (!logs.TryGetProperty("resourceLogs", out var resources))
        {
            return records;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            var service = ServiceOf(resource) ?? string.Empty;
            if (!resource.TryGetProperty("scopeLogs", out var scopes))
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray())
            {
                if (!scope.TryGetProperty("logRecords", out var entries))
                {
                    continue;
                }

                foreach (var record in entries.EnumerateArray())
                {
                    var message = record.TryGetProperty("body", out var body)
                        && body.TryGetProperty("stringValue", out var text)
                            ? text.GetString()
                            : null;
                    records.Add((service, message, record));
                }
            }
        }

        return records;
    }

    private static IEnumerable<JsonElement> SpansOf(JsonElement spans)
    {
        List<JsonElement> entries = [];
        if (!spans.TryGetProperty("resourceSpans", out var resources))
        {
            return entries;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            if (!resource.TryGetProperty("scopeSpans", out var scopes))
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.TryGetProperty("spans", out var spansOfScope))
                {
                    entries.AddRange(spansOfScope.EnumerateArray());
                }
            }
        }

        return entries;
    }

    private static bool CarriesJob(JsonElement record, string jobId) =>
        record.TryGetProperty("attributes", out var attributes)
        && attributes.EnumerateArray().Any(attribute =>
            attribute.GetProperty("key").GetString() is "job_id" or "job.id"
            && attribute.GetProperty("value").TryGetProperty("stringValue", out var value)
            && value.GetString() == jobId);

    private static HashSet<string> ServicesOfTrace(JsonElement spans, string traceId)
    {
        var services = new HashSet<string>(StringComparer.Ordinal);
        if (!spans.TryGetProperty("resourceSpans", out var resources))
        {
            return services;
        }

        foreach (var resource in resources.EnumerateArray())
        {
            var service = ServiceOf(resource) ?? string.Empty;
            if (!resource.TryGetProperty("scopeSpans", out var scopes))
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray())
            {
                if (!scope.TryGetProperty("spans", out var entries))
                {
                    continue;
                }

                foreach (var span in entries.EnumerateArray())
                {
                    if (span.TryGetProperty("traceId", out var id) && id.GetString() == traceId)
                    {
                        services.Add(service);
                    }
                }
            }
        }

        return services;
    }
}
