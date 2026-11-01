using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using ReportService.ServiceDefaults;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Testing;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The development-only telemetry endpoints of the API, hosted in memory: they
/// are absent unless their flag is on, and when they are on they answer with the
/// capture body and with the trace id of the request's own span.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class TelemetryEndpointTests : IDisposable
{
    private readonly TemporaryDirectory _storage = new();

    public void Dispose() => _storage.Dispose();

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheEndpointsAreAbsentUnlessTheDiagnosticsFlagIsOn()
    {
        await using var api = Api(diagnostics: false);
        using var client = api.CreateClient();

        using var metrics = await client.GetAsync(new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative), Cancellation);
        using var trace = await client.GetAsync(new Uri(TelemetryDiagnostics.TraceRoute, UriKind.Relative), Cancellation);
        using var unit = await client.PostAsJsonAsync(
            new Uri(TelemetryDiagnostics.UnitOfWorkRoute, UriKind.Relative),
            new { jobId = "synthetic-3a1f" },
            Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, trace.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unit.StatusCode);
    }

    [Fact]
    public async Task TheCaptureEndpointAnswersWithTheProcessResourceInstrumentsAndPoints()
    {
        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();
        api.Services.GetRequiredService<MeterProvider>().ForceFlush();

        using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative), Cancellation);
        var json = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(json);
        var service = body.RootElement.GetProperty("service");
        Assert.Equal(ServiceNames.Api, service.GetProperty("name").GetString());
        Assert.Equal(ProductVersion.Informational, service.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(service.GetProperty("instanceId").GetString()));
        Assert.Equal("Development", service.GetProperty("environment").GetString());
        Assert.Equal(1000, body.RootElement.GetProperty("exportIntervalMilliseconds").GetInt32());
        Assert.Contains(
            body.RootElement.GetProperty("instruments").EnumerateArray(),
            instrument => instrument.GetProperty("name").GetString() == BuildInfoMetric.InstrumentName);
        Assert.True(body.RootElement.GetProperty("omittedPoints").GetInt64() >= 0);
    }

    [Fact]
    public async Task TheTraceEndpointAnswersWithTheRequestsOwnTraceId()
    {
        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>(new Uri(TelemetryDiagnostics.TraceRoute, UriKind.Relative), Cancellation);

        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task AUnitOfWorkAnswersWithItsSpanAndRefusesAnIdentifierOffTheGrammar()
    {
        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();

        using var accepted = await client.PostAsJsonAsync(
            new Uri(TelemetryDiagnostics.UnitOfWorkRoute, UriKind.Relative),
            new { jobId = "synthetic-3a1f" },
            Cancellation);
        using var refused = await client.PostAsJsonAsync(
            new Uri(TelemetryDiagnostics.UnitOfWorkRoute, UriKind.Relative),
            new { jobId = "Synthetic Not An Identifier" },
            Cancellation);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>(Cancellation);
        Assert.Equal("synthetic-3a1f", body.GetProperty("jobId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("traceId").GetString());
        Assert.Matches("^[0-9a-f]{16}$", body.GetProperty("spanId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("telemetry.invalid-job", await refused.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePropagationRouteRefusesWithNoWorkerAddressWhenTheCompositionGaveNone()
    {
        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.PropagateRoute, UriKind.Relative), Cancellation);
        var body = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("telemetry.no-worker", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePropagationRouteReadsTheAddressFromTheEntryTheCompositionSets()
    {
        // The composition sets one environment entry per reference, and a process reads the
        // environment through its configuration: this test states the entry the way the
        // composition does and expects the route to have it, so an entry the platform writes
        // and the process cannot read is a failure here rather than in the composition.
        Environment.SetEnvironmentVariable(TelemetryDiagnostics.OrchestrationWorkerAddressEntry, "http://127.0.0.1:1");
        try
        {
            await using var api = Api(diagnostics: true);
            using var client = api.CreateClient();

            using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.PropagateRoute, UriKind.Relative), Cancellation);
            var body = await response.Content.ReadAsStringAsync(Cancellation);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("telemetry.worker-unreachable", body, StringComparison.Ordinal);
            Assert.DoesNotContain("telemetry.no-worker", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryDiagnostics.OrchestrationWorkerAddressEntry, null);
        }
    }

    [Fact]
    public async Task ThePropagationRouteRefusesAMalformedWorkerAddressWithTheDesignedRefusal()
    {
        // The composition can state an address the request cannot turn into one: the route
        // keeps its designed refusal instead of escaping as an unhandled fault.
        Environment.SetEnvironmentVariable(TelemetryDiagnostics.OrchestrationWorkerAddressEntry, "not-an-absolute-address");
        try
        {
            await using var api = Api(diagnostics: true);
            using var client = api.CreateClient();

            using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.PropagateRoute, UriKind.Relative), Cancellation);
            var body = await response.Content.ReadAsStringAsync(Cancellation);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("telemetry.worker-unreachable", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TelemetryDiagnostics.OrchestrationWorkerAddressEntry, null);
        }
    }

    private WebApplicationFactory<Program> Api(bool diagnostics) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Filesystem:Root", Path.Combine(_storage.Path, "storage"));
            builder.UseSetting(DatabaseOptions.ConnectionStringSetting, MetadataDatabaseStub.ConnectionString);
            builder.UseSetting(DatabaseOptions.ExpectedSchemaVersionSetting, MetadataDatabaseStub.ExpectedVersion);
            builder.UseSetting(TelemetryDiagnosticsSettings.EnabledSetting, diagnostics ? "true" : "false");
            builder.UseSetting(TelemetryDiagnosticsSettings.ExportIntervalSetting, "1000");
            builder.ConfigureTestServices(services => MetadataDatabaseStub.UseHealthy(services));
        });
}
