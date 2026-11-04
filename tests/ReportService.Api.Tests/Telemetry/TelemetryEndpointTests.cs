using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using ReportService.Api.Http;
using ReportService.Api.Telemetry;
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

    /// <summary>The instruments the product declares, by the names it has always exported.</summary>
    private static readonly string[] ProductInstrumentNames =
    [
        BuildInfoMetric.InstrumentName,
        JobAcceptanceMetrics.InstrumentName,
        ConsumerRefusalMetrics.InstrumentName,
    ];

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
    public async Task TheProductMeterKeepsItsScopeNameItsInstrumentsTheirUnitsAndTypes()
    {
        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();

        api.Services.GetRequiredService<JobAcceptanceMetrics>().Accepted();
        api.Services.GetRequiredService<ConsumerRefusalMetrics>().Refusal(
            ErrorCodes.ConsumerUnknown,
            ConsumerRefusalMetrics.CreateOperation,
            "missing");
        api.Services.GetRequiredService<MeterProvider>().ForceFlush();

        using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative), Cancellation);
        var json = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(json);
        var instruments = body.RootElement.GetProperty("instruments").EnumerateArray()
            .Select(instrument => (
                Name: instrument.GetProperty("name").GetString()!,
                Meter: instrument.GetProperty("meter").GetString()!,
                Unit: instrument.GetProperty("unit").GetString()!,
                Type: instrument.GetProperty("type").GetString()!))
            .ToList();

        Assert.All(
            instruments.Where(instrument => ProductInstrumentNames.Contains(instrument.Name)),
            instrument => Assert.Equal(TelemetryDefaults.MeterName, instrument.Meter));

        // The closed table of the meter that scope name names: the API's three instruments,
        // each with the quantity it has always carried, and no fourth.
        var expected = new (string Name, string Unit, string Type)[]
        {
            (BuildInfoMetric.InstrumentName, string.Empty, "Gauge"),
            (JobAcceptanceMetrics.InstrumentName, "{request}", "Sum"),
            (ConsumerRefusalMetrics.InstrumentName, "{request}", "Sum"),
        };
        Assert.Equal(
            expected.OrderBy(row => row.Name, StringComparer.Ordinal),
            instruments
                .Where(instrument => instrument.Meter == TelemetryDefaults.MeterName)
                .Select(instrument => (instrument.Name, instrument.Unit, instrument.Type))
                .OrderBy(row => row.Name, StringComparer.Ordinal));

        var gauge = Assert.Single(
            body.RootElement.GetProperty("points").EnumerateArray(),
            point => point.GetProperty("instrument").GetString() == BuildInfoMetric.InstrumentName);
        Assert.Equal(
            ["runtime", "version"],
            gauge.GetProperty("tags").EnumerateObject().Select(tag => tag.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheMetricsReadAnswersWithAPointWhoseBoundIsNotFinite()
    {
        // A number is written as the number it is, and a member the point does not carry stays
        // out of the body. This half reads a capture the case builds itself — no host, no reader
        // beside it and nothing that exports into it — so every input of the text is fixed here
        // and no run can move it. It goes first: it holds the text a finite value is written
        // with, before either reader of the host below exists.
        var control = new TelemetryCaptureBuffer();
        control.SetResource(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["service.name"] = ServiceNames.Api,
            ["service.version"] = "1.0.0",
            ["service.instance.id"] = "instance-1",
            ["deployment.environment"] = "Development",
        });
        var invoice = new KeyValuePair<string, string?>("page.kind", "invoice");
        var controlInstruments = new CapturedInstrument[]
        {
            new("report.queue.depth", TelemetryDefaults.MeterName, "Gauge", "{item}"),
            new("report.render.duration", TelemetryDefaults.MeterName, "Histogram", "ms"),
            new("report.upload.bytes", TelemetryDefaults.MeterName, "Sum", "By"),
        };
        var controlPoints = new CapturedPoint[]
        {
            new(
                "report.queue.depth",
                "Gauge",
                "{item}",
                1d,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
            new(
                "report.render.duration",
                "Histogram",
                "ms",
                null,
                null,
                3,
                6.5,
                1.25,
                2.75,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
            new(
                "report.upload.bytes",
                "Sum",
                "By",
                42.5,
                true,
                null,
                42.5,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
        };
        control.Record(controlInstruments, controlPoints);

        var finite = control.Take(1000).ToJson();

        // The whole point object, character for character: the member order, the member set and
        // every finite number — the gauge's value, the histogram's count, sum, min and max, and
        // the sum's value and sum. A member written where the point carries none would change
        // the text and fail here.
        Assert.Contains(
            "{\"instrument\":\"report.queue.depth\",\"type\":\"Gauge\",\"unit\":\"{item}\",\"value\":1,"
            + "\"startTime\":\"1970-01-01T00:00:00.0000000Z\",\"endTime\":\"1970-01-01T00:00:02.0000000Z\","
            + "\"tags\":{\"page.kind\":\"invoice\"}}",
            finite,
            StringComparison.Ordinal);
        Assert.Contains(
            "{\"instrument\":\"report.render.duration\",\"type\":\"Histogram\",\"unit\":\"ms\",\"count\":3,"
            + "\"sum\":6.5,\"min\":1.25,\"max\":2.75,"
            + "\"startTime\":\"1970-01-01T00:00:00.0000000Z\",\"endTime\":\"1970-01-01T00:00:02.0000000Z\","
            + "\"tags\":{\"page.kind\":\"invoice\"}}",
            finite,
            StringComparison.Ordinal);
        Assert.Contains(
            "{\"instrument\":\"report.upload.bytes\",\"type\":\"Sum\",\"unit\":\"By\",\"value\":42.5,"
            + "\"monotonic\":true,\"sum\":42.5,"
            + "\"startTime\":\"1970-01-01T00:00:00.0000000Z\",\"endTime\":\"1970-01-01T00:00:02.0000000Z\","
            + "\"tags\":{\"page.kind\":\"invoice\"}}",
            finite,
            StringComparison.Ordinal);

        await using var api = Api(diagnostics: true);
        using var client = api.CreateClient();

        // The host's own points reach the retention before the case records its own, so the four
        // below are appended after them. The retention holds two thousand and this case records
        // four, so nothing is dropped and no ordering question arises.
        api.Services.GetRequiredService<MeterProvider>().ForceFlush();

        var capture = api.Services.GetRequiredService<TelemetryCaptureBuffer>();
        var declared = new CapturedInstrument[]
        {
            new("report.render.duration", TelemetryDefaults.MeterName, "Histogram", "ms"),
            new("report.render.pages", TelemetryDefaults.MeterName, "Histogram", "{page}"),
            new("report.upload.bytes", TelemetryDefaults.MeterName, "Sum", "By"),
            new("report.queue.depth", TelemetryDefaults.MeterName, "Gauge", "{item}"),
        };
        var recorded = new CapturedPoint[]
        {
            // An interval that recorded no measurement: the exporter reports that the point has
            // bounds and hands over its unset ones, so both are numbers JSON cannot carry. What
            // it did measure is zero, and zero is finite on purpose: it has to come back.
            new(
                "report.render.duration",
                "Histogram",
                "ms",
                null,
                null,
                0,
                0d,
                double.NaN,
                double.NaN,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
            // A finite lower bound beside an upper one JSON cannot carry.
            new(
                "report.render.pages",
                "Histogram",
                "{page}",
                null,
                null,
                3,
                6d,
                1d,
                double.PositiveInfinity,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
            // The exporter leaves a sum's value to the measurement it copied, and no instrument of
            // this process records a number that is not finite, so a running host does not reach
            // this shape today: it states what the read path does with any point the capture
            // holds, here a sum whose value and sum carry no finite number.
            new(
                "report.upload.bytes",
                "Sum",
                "By",
                double.PositiveInfinity,
                true,
                null,
                double.PositiveInfinity,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
            // The same for a gauge: its last value is no finite number either, and the exporter
            // does not currently hand the capture that shape.
            new(
                "report.queue.depth",
                "Gauge",
                "{item}",
                double.NegativeInfinity,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                [invoice]),
        };
        capture.Record(declared, recorded);

        // The snapshot is taken before the request, so a later export of the reader can only add
        // points to the body: the comparison below is an inclusion, never a tolerance.
        var retained = capture.Take(1000);

        using var response = await client.GetAsync(new Uri(TelemetryDiagnostics.MetricsRoute, UriKind.Relative), Cancellation);
        var json = await response.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(json);
        var points = body.RootElement.GetProperty("points").EnumerateArray().ToList();

        // The interval that recorded nothing: its measured values come back as they were
        // measured, and each of the two bounds it reports is written as null — not as a number,
        // not as a string, not as an absent member. A bound a JSON number cannot carry is written
        // as null, so the read answers with the point it retained instead of failing.
        var empty = Recorded(points, "report.render.duration");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("min").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("max").ValueKind);
        Assert.Equal(0L, empty.GetProperty("count").GetInt64());
        Assert.Equal(0d, empty.GetProperty("sum").GetDouble());
        Assert.False(empty.TryGetProperty("value", out _));
        Assert.False(empty.TryGetProperty("monotonic", out _));

        // One finite bound beside the one JSON cannot carry.
        var bounded = Recorded(points, "report.render.pages");
        Assert.Equal(3L, bounded.GetProperty("count").GetInt64());
        Assert.Equal(6d, bounded.GetProperty("sum").GetDouble());
        Assert.Equal(1d, bounded.GetProperty("min").GetDouble());
        Assert.Equal(JsonValueKind.Null, bounded.GetProperty("max").ValueKind);
        Assert.False(bounded.TryGetProperty("value", out _));
        Assert.False(bounded.TryGetProperty("monotonic", out _));

        // The sum writes both of its numbers as null and keeps the monotonic flag it carries, so
        // "present" is not an empty claim beside the members it does not have.
        var sum = Recorded(points, "report.upload.bytes");
        Assert.Equal(JsonValueKind.Null, sum.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, sum.GetProperty("sum").ValueKind);
        Assert.Equal(JsonValueKind.True, sum.GetProperty("monotonic").ValueKind);
        Assert.False(sum.TryGetProperty("count", out _));
        Assert.False(sum.TryGetProperty("min", out _));
        Assert.False(sum.TryGetProperty("max", out _));

        // The gauge carries one value and none of the members it does not have.
        var gauge = Recorded(points, "report.queue.depth");
        Assert.Equal(JsonValueKind.Null, gauge.GetProperty("value").ValueKind);
        Assert.False(gauge.TryGetProperty("count", out _));
        Assert.False(gauge.TryGetProperty("sum", out _));
        Assert.False(gauge.TryGetProperty("min", out _));
        Assert.False(gauge.TryGetProperty("max", out _));
        Assert.False(gauge.TryGetProperty("monotonic", out _));

        // Nothing was dropped and nothing was altered to make the body writable: every point the
        // capture retained before the request is in the body with what it held.
        Assert.Equal(0L, body.RootElement.GetProperty("omittedPoints").GetInt64());
        Assert.True(
            points.Count >= retained.Points.Count,
            $"the body carries {points.Count} points and the capture retained {retained.Points.Count}");
        foreach (var expected in retained.Points)
        {
            Assert.True(
                points.Any(point => SamePoint(expected, point)),
                $"the body carries no point matching what the capture retained: {Describe(expected)}");
        }

        // The point the case recorded, matched by its instrument and by the tag set it carried.
        static JsonElement Recorded(IReadOnlyList<JsonElement> points, string instrument)
        {
            var matches = points
                .Where(point => point.GetProperty("instrument").GetString() == instrument)
                .Where(point => BodyTags(point) == "page.kind=invoice")
                .ToList();
            Assert.True(matches.Count == 1, $"the body carries {matches.Count} points of {instrument} with the case's tag");
            return matches[0];
        }

        // A retained point and a written point hold the same thing: the identity the body carries
        // and every member, a finite number as the number it is and one JSON cannot carry as null.
        static bool SamePoint(CapturedPoint retained, JsonElement point) =>
            point.GetProperty("instrument").GetString() == retained.Instrument
            && point.GetProperty("type").GetString() == retained.Type
            && point.GetProperty("unit").GetString() == retained.Unit
            && BodyTags(point) == TagsOf(retained)
            && SameMeasurement(retained.Value, point, "value")
            && SameMeasurement(retained.Sum, point, "sum")
            && SameMeasurement(retained.Min, point, "min")
            && SameMeasurement(retained.Max, point, "max")
            && SameCount(retained.Count, point)
            && SameMonotonic(retained.Monotonic, point);

        static bool SameMeasurement(double? measurement, JsonElement point, string name)
        {
            if (measurement is not { } number)
            {
                return !point.TryGetProperty(name, out _);
            }

            if (!point.TryGetProperty(name, out var written))
            {
                return false;
            }

            return double.IsFinite(number)
                ? written.ValueKind == JsonValueKind.Number && written.GetDouble() == number
                : written.ValueKind == JsonValueKind.Null;
        }

        static bool SameCount(long? count, JsonElement point) =>
            count is { } measured
                ? point.TryGetProperty("count", out var written) && written.GetInt64() == measured
                : !point.TryGetProperty("count", out _);

        static bool SameMonotonic(bool? monotonic, JsonElement point) =>
            monotonic is { } flag
                ? point.TryGetProperty("monotonic", out var written) && written.GetBoolean() == flag
                : !point.TryGetProperty("monotonic", out _);

        static string BodyTags(JsonElement point) =>
            string.Join(
                ',',
                point.GetProperty("tags").EnumerateObject()
                    .Select(tag => tag.Name + "=" + tag.Value.GetString())
                    .Order(StringComparer.Ordinal));

        static string TagsOf(CapturedPoint point) =>
            string.Join(
                ',',
                point.Tags.Select(tag => tag.Key + "=" + tag.Value).Order(StringComparer.Ordinal));

        static string Describe(CapturedPoint point) => $"{point.Instrument} ({TagsOf(point)})";
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
