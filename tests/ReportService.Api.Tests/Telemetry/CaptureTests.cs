using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The development-only metric capture: absent unless its flag is on, a pure
/// observer of the points the pipeline exports, and bounded in what it retains
/// and in the size of what it returns.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class CaptureTests
{
    [Fact]
    public async Task TheCaptureExistsOnlyWhenTheDiagnosticsFlagIsOn()
    {
        await using (var off = await TelemetryTestHost.StartAsync(diagnostics: false))
        {
            Assert.Null(off.Services.GetService<TelemetryCaptureBuffer>());
            off.CollectMetrics();
        }

        await using var on = await TelemetryTestHost.StartAsync(diagnostics: true);
        Assert.NotNull(on.Capture);
    }

    [Fact]
    public async Task TheCaptureSeesWhatThePipelineExportedWithoutChangingIt()
    {
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);
        host.Services.GetRequiredService<BuildInfoMetric>().Record();

        // Both readers export from the same pipeline on this one collect, and the capture's own
        // reader is registered first, so its window closes inside the in-memory exporter's.
        host.CollectMetrics();
        var captured = host.Capture.Take(1000).Points;
        var exported = Read(host.Metrics);

        Assert.NotEmpty(captured);
        Assert.NotEmpty(exported);
        Assert.Equal(Keys(exported), Keys(captured));

        foreach (var observed in exported)
        {
            var matching = captured
                .Where(candidate => candidate.Instrument == observed.Instrument && TagsOf(candidate) == TagsOf(observed))
                .OrderByDescending(candidate => candidate.EndTime)
                .ToList();
            var point = Assert.Single(matching.Take(1));

            Assert.Equal(observed.Type, point.Type);
            Assert.Equal(observed.Unit, point.Unit);
            Assert.Equal(observed.Value, point.Value);

            // Two readers of different cadence stamp their own window, so the windows are compared
            // as observations of the same period — never by equal timestamps. The capture's reader
            // is registered first, so its window closes no later than the in-memory exporter's, and
            // it lies inside the period that exporter reports up to its own end.
            Assert.True(
                point.EndTime <= observed.EndTime,
                $"{observed.Instrument}: the capture's window ends at {point.EndTime:O}, after {observed.EndTime:O}.");
            Assert.True(
                point.StartTime <= observed.EndTime,
                $"{observed.Instrument}: the capture's window starts at {point.StartTime:O}, after the other reader's end {observed.EndTime:O}.");
        }

        Assert.DoesNotContain(captured, point => point.Tags.Any(tag => tag.Key.Contains("job", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NoInstrumentOfTheProductMeterDeclaresAJobDimension()
    {
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);
        host.Services.GetRequiredService<BuildInfoMetric>().Record();

        host.CollectMetrics();
        var instruments = host.Capture.Take(1000).Instruments;

        var product = instruments.Where(instrument => instrument.Meter == TelemetryDefaults.MeterName).ToList();
        Assert.Equal([BuildInfoMetric.InstrumentName], product.Select(instrument => instrument.Name));
        Assert.All(instruments, instrument => Assert.DoesNotContain("job", instrument.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(BuildInfoMetric.Tags, tag => tag.Key.Contains("job", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheRetentionKeepsTheNewestTwoThousandPointsAndDropsTheOldest()
    {
        var buffer = new TelemetryCaptureBuffer();

        buffer.Record([], Enumerable.Range(0, 2500).Select(index => Point("point-" + index.ToString(CultureInfo.InvariantCulture))));

        Assert.Equal(TelemetryCaptureBuffer.Capacity, buffer.RetainedPoints);
        Assert.Equal(500, buffer.OmittedPoints);
        var read = buffer.Take(2000);
        Assert.Equal("point-500", read.Points[0].Tags.Single(tag => tag.Key == "index").Value);
        Assert.Equal("point-2499", read.Points[^1].Tags.Single(tag => tag.Key == "index").Value);
        Assert.Equal(2000, read.ExportIntervalMilliseconds);
    }

    [Fact]
    public void TheResponseBodyStaysWithinItsCapAndReportsWhatItOmitted()
    {
        var buffer = new TelemetryCaptureBuffer();
        buffer.SetResource(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["service.name"] = ServiceNames.Api,
            ["service.version"] = ProductVersion.Informational,
        });
        buffer.Record(
            [new CapturedInstrument(BuildInfoMetric.InstrumentName, TelemetryDefaults.MeterName, "Gauge", string.Empty)],
            Enumerable.Range(0, TelemetryCaptureBuffer.Capacity)
                .Select(index => Point("point-" + index.ToString(CultureInfo.InvariantCulture), new string('v', 400))));

        var body = buffer.Take(2000).ToJson();

        Assert.True(Encoding.UTF8.GetByteCount(body) <= TelemetryCaptureBuffer.MaxBodyBytes);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(ServiceNames.Api, document.RootElement.GetProperty("service").GetProperty("name").GetString());
        Assert.Equal(BuildInfoMetric.InstrumentName, document.RootElement.GetProperty("instruments")[0].GetProperty("name").GetString());
        Assert.True(document.RootElement.GetProperty("omittedPoints").GetInt64() > 0);
    }

    private static CapturedPoint Point(string index, string value = "1") => new(
        BuildInfoMetric.InstrumentName,
        "Gauge",
        string.Empty,
        1d,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddSeconds(2),
        [new KeyValuePair<string, string?>("index", index), new KeyValuePair<string, string?>("value", value)]);

    private static string Keys(IEnumerable<CapturedPoint> points) =>
        string.Join('|', points.Select(point => point.Instrument + "/" + TagsOf(point)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static string TagsOf(CapturedPoint point) =>
        string.Join(',', point.Tags.Select(tag => tag.Key + "=" + tag.Value).Order(StringComparer.Ordinal));

    private static List<CapturedPoint> Read(IEnumerable<Metric> metrics)
    {
        List<CapturedPoint> points = [];
        foreach (var metric in metrics)
        {
            var type = TypeName(metric.MetricType);
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                double? value = null;
                long? count = null;
                double? sum = null;
                if (metric.MetricType.IsGauge())
                {
                    value = metric.MetricType.IsDouble() ? point.GetGaugeLastValueDouble() : point.GetGaugeLastValueLong();
                }
                else if (metric.MetricType.IsSum())
                {
                    value = metric.MetricType.IsDouble() ? point.GetSumDouble() : point.GetSumLong();
                }
                else if (metric.MetricType.IsHistogram())
                {
                    count = point.GetHistogramCount();
                    sum = point.GetHistogramSum();
                }

                List<KeyValuePair<string, string?>> tags = [];
                foreach (var tag in point.Tags)
                {
                    tags.Add(new KeyValuePair<string, string?>(tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));
                }

                points.Add(new CapturedPoint(
                    metric.Name,
                    type,
                    metric.Unit ?? string.Empty,
                    value,
                    null,
                    count,
                    sum,
                    null,
                    null,
                    point.StartTime,
                    point.EndTime,
                    tags));
            }
        }

        return points;
    }

    private static string TypeName(MetricType type) => type switch
    {
        MetricType.LongGauge or MetricType.DoubleGauge => "Gauge",
        MetricType.LongSum or MetricType.DoubleSum or MetricType.LongSumNonMonotonic or MetricType.DoubleSumNonMonotonic => "Sum",
        MetricType.Histogram or MetricType.ExponentialHistogram => "Histogram",
        _ => type.ToString(),
    };
}
