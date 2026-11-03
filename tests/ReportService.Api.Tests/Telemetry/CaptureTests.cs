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

        // One collect, exported by two readers that close their own window: the capture's reader
        // is registered first, so its window closes inside the in-memory exporter's. What the
        // collect holds of this host's own meter is therefore written before either window
        // closes, while the framework instruments the whole process writes are not.
        host.CollectMetrics();
        var read = host.Capture.Take(1000);
        var captured = read.Points;
        var exported = Read(host.Metrics);

        Assert.NotEmpty(captured);
        Assert.NotEmpty(exported);
        AssertPassThrough(read, exported, host.Identity);

        Assert.DoesNotContain(captured, point => point.Tags.Any(tag => tag.Key.Contains("job", StringComparison.OrdinalIgnoreCase)));

        // The capture is the host's own meter: the product instrument this host declares
        // carries its identity, and no tag carries the identity itself or a job identifier.
        var identity = host.Identity.Name;
        Assert.All(
            read.Instruments.Where(instrument => instrument.Name == BuildInfoMetric.InstrumentName),
            instrument => Assert.Equal(identity, instrument.Meter));
        Assert.DoesNotContain(captured, point => point.Tags.Any(tag => tag.Value == identity));
    }

    [Fact]
    public async Task TheComparisonRejectsACaptureThatAltersWhatThePipelineExported()
    {
        // The premise is the case's own: a real read of a real host is accepted before anything
        // is doctored, so the pairs below differ in exactly one field.
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);
        host.Services.GetRequiredService<BuildInfoMetric>().Record();
        host.CollectMetrics();
        var read = host.Capture.Take(1000);
        var exported = Read(host.Metrics);
        var recorded = Expected[0];

        AssertPassThrough(read, exported, host.Identity);

        // A captured value that no longer matches what the case recorded.
        var altered = read.Points.Select(point => SameKey(point, recorded) ? point with { Value = 2d } : point).ToList();
        var valueFailure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertPassThrough(read with { Points = altered }, exported, host.Identity));
        Assert.Contains(recorded.Instrument, valueFailure.Message, StringComparison.Ordinal);
        Assert.Contains(TagsOf(recorded), valueFailure.Message, StringComparison.Ordinal);
        Assert.Contains("the recorded value is 1", valueFailure.Message, StringComparison.Ordinal);
        Assert.Contains("the captured value is 2", valueFailure.Message, StringComparison.Ordinal);

        // The point the case recorded, missing from the capture altogether.
        var removed = read.Points.Where(point => !SameKey(point, recorded)).ToList();
        var missingFailure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertPassThrough(read with { Points = removed }, exported, host.Identity));
        Assert.Contains(recorded.Instrument, missingFailure.Message, StringComparison.Ordinal);
        Assert.Contains(TagsOf(recorded), missingFailure.Message, StringComparison.Ordinal);

        // A point outside this host's meter, which the two readers hold with different units. It
        // is built here rather than taken from the run, so the check never depends on whether the
        // framework happened to publish that instrument while this case ran.
        var synthetic = new CapturedPoint(
            "dotnet.gc.collections",
            "Sum",
            "count",
            3d,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(2),
            [new KeyValuePair<string, string?>("generation", "0")]);
        var alteredUnit = synthetic with { Unit = "counts" };
        var capturedWithUnit = read.Points.Append(alteredUnit).ToList();
        var exportedWithUnit = exported.Append(new ExportedPoint(TelemetryDefaults.MeterName, synthetic)).ToList();
        var unitFailure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertPassThrough(read with { Points = capturedWithUnit }, exportedWithUnit, host.Identity));
        Assert.Contains(synthetic.Instrument, unitFailure.Message, StringComparison.Ordinal);
        Assert.Contains(TagsOf(synthetic), unitFailure.Message, StringComparison.Ordinal);
        Assert.Contains("the exported unit is count", unitFailure.Message, StringComparison.Ordinal);
        Assert.Contains("the captured unit is counts", unitFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoInstrumentOfTheProductMeterDeclaresAJobDimension()
    {
        await using var host = await TelemetryTestHost.StartAsync(diagnostics: true);
        host.Services.GetRequiredService<BuildInfoMetric>().Record();

        host.CollectMetrics();
        var read = host.Capture.Take(1000);
        var instruments = read.Instruments;

        var product = instruments.Where(instrument => instrument.Meter == host.Identity.Name).ToList();
        Assert.Equal([BuildInfoMetric.InstrumentName], product.Select(instrument => instrument.Name));
        Assert.All(instruments, instrument => Assert.DoesNotContain("job", instrument.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(BuildInfoMetric.Tags, tag => tag.Key.Contains("job", StringComparison.OrdinalIgnoreCase));

        // No instrument of another host's meter is captured: the instrument this host
        // declares carries its own identity as its meter.
        Assert.All(
            instruments.Where(instrument => instrument.Name == BuildInfoMetric.InstrumentName),
            instrument => Assert.Equal(host.Identity.Name, instrument.Meter));

        // The same in one line for the name a process that owns its host exports: no
        // instrument carries it unless that is this host's own identity.
        if (host.Identity.Name != TelemetryDefaults.MeterName)
        {
            Assert.DoesNotContain(instruments, instrument => instrument.Meter == TelemetryDefaults.MeterName);
        }
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

    /// <summary>
    /// What the case itself records, once, before the collect: the points of this host's own
    /// meter, so both readers are held against a value neither of them decides. A product
    /// instrument added to the host's meter adds its row here in the same change; the day it does
    /// not, the case fails and names the key it expected. The expectation carries no window —
    /// only the instrument, the type, the unit, the value and the tags are compared — so its
    /// times are placeholders.
    /// </summary>
    private static readonly CapturedPoint[] Expected =
    [
        new(
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
            DateTimeOffset.UnixEpoch,
            BuildInfoMetric.Tags
                .Select(tag => new KeyValuePair<string, string?>(tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture)))
                .ToList()),
    ];

    /// <summary>One point as a reader exported it, with the meter the point alone does not carry.</summary>
    private readonly record struct ExportedPoint(string Meter, CapturedPoint Point);

    /// <summary>
    /// Asserts that the capture holds what the pipeline exported and altered nothing: the points
    /// of this host's own meter equal the expectation the case recorded, on both readers, and
    /// every point the capture holds keeps the type and unit its instrument declares. The
    /// instruments the whole process writes are compared for what a window cannot move — the same
    /// key is the same instrument, type and unit in either reader — and never for their key sets
    /// or their values, which follow the moment each reader closed.
    /// </summary>
    private static void AssertPassThrough(CapturedMetrics read, IReadOnlyList<ExportedPoint> exported, MeterIdentity identity)
    {
        // The domain is the host's own meter, read from the capture's own declarations: the
        // instruments it says it holds, not a name this case assumes.
        var declared = read.Instruments.Where(instrument => instrument.Meter == identity.Name).ToList();
        Assert.True(declared.Count > 0, $"The host's meter {identity.Name} declares no instrument in the capture.");
        var names = declared.Select(instrument => instrument.Name).Distinct(StringComparer.Ordinal).ToList();

        // A name this meter declares belongs to no other: a point is joined to this host by the
        // name its instrument carries, so a collision stops the case rather than joining a point
        // to the wrong meter.
        var elsewhere = read.Instruments
            .Where(instrument => instrument.Meter != identity.Name && names.Contains(instrument.Name))
            .Select(instrument => $"{instrument.Name} ({instrument.Meter})")
            .ToList();
        Assert.True(elsewhere.Count == 0, $"{string.Join(", ", elsewhere)} is declared by a meter other than {identity.Name}.");

        var capturedOwn = read.Points.Where(point => names.Contains(point.Instrument)).ToList();
        var exportedOwn = exported.Where(point => point.Meter == identity.Name).ToList();

        // The capture holds what the case recorded, and the pipeline exported the same thing: the
        // capture is a faithful pass-through of its own pipeline.
        AssertHoldsExpectation(capturedOwn, "The capture");
        AssertHoldsExpectation([.. exportedOwn.Select(point => point.Point)], "The pipeline's export");

        // The capture's window lies inside the pipeline's: the case recorded this point before the
        // collect, so a window of the capture that closes no later than the pipeline's must hold
        // it. A later window of the capture — the reader also exports on its own interval — is a
        // second observation of the same point and is not the one compared here.
        foreach (var recorded in Expected)
        {
            var pipeline = exportedOwn
                .Where(point => SameKey(point.Point, recorded))
                .OrderByDescending(point => point.Point.EndTime)
                .First();
            var inside = capturedOwn
                .Where(point => SameKey(point, recorded) && point.EndTime <= pipeline.Point.EndTime)
                .ToList();
            Assert.True(
                inside.Count > 0,
                $"{Describe(recorded)}: the capture holds no point whose window closes no later than the pipeline's, which ends at {pipeline.Point.EndTime:O}.");
            var capturedPoint = inside.OrderByDescending(point => point.EndTime).First();
            Assert.True(
                capturedPoint.StartTime <= pipeline.Point.EndTime,
                $"{Describe(recorded)}: the capture's window starts at {capturedPoint.StartTime:O}, after the pipeline's window ends at {pipeline.Point.EndTime:O}.");
        }

        // Nothing the capture holds was altered: wherever both readers hold a point with the same
        // instrument and tags, the type and the unit are the instrument's own and do not move with
        // a window. The value is deliberately not compared here — a framework series is written
        // between the two reads, so its value follows the schedule and not the capture.
        foreach (var capturedPoint in read.Points)
        {
            foreach (var observed in exported.Where(point => SameKey(point.Point, capturedPoint)))
            {
                AssertSame(Describe(capturedPoint), "exported type", observed.Point.Type, "captured type", capturedPoint.Type);
                AssertSame(Describe(capturedPoint), "exported unit", observed.Point.Unit, "captured unit", capturedPoint.Unit);
            }
        }
    }

    /// <summary>Asserts that one reader's points carry exactly the rows the case recorded, with the value each records.</summary>
    private static void AssertHoldsExpectation(IReadOnlyList<CapturedPoint> points, string reader)
    {
        Assert.True(
            points.Count > 0,
            $"{reader} holds no point of {string.Join(", ", Expected.Select(recorded => Describe(recorded)))}.");
        Assert.Equal(Keys(Expected), Keys(points));

        foreach (var recorded in Expected)
        {
            var point = points
                .Where(candidate => SameKey(candidate, recorded))
                .OrderByDescending(candidate => candidate.EndTime)
                .First();
            var described = Describe(recorded);
            AssertSame(described, "recorded type", recorded.Type, "captured type", point.Type);
            AssertSame(described, "recorded unit", recorded.Unit, "captured unit", point.Unit);
            AssertSame(described, "recorded value", recorded.Value, "captured value", point.Value);
        }
    }

    /// <summary>
    /// Asserts two values are equal and, where they are not, says which instrument and tags the
    /// two came from: a failure has to name what differed, not only the two values.
    /// </summary>
    private static void AssertSame<T>(string described, string recordedName, T recorded, string observedName, T observed)
    {
        try
        {
            Assert.Equal(recorded, observed);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            throw new Xunit.Sdk.XunitException(
                $"{described}: the {recordedName} is {recorded} and the {observedName} is {observed}.",
                failure);
        }
    }

    private static bool SameKey(CapturedPoint candidate, CapturedPoint expected) =>
        candidate.Instrument == expected.Instrument && TagsOf(candidate) == TagsOf(expected);

    private static string Describe(CapturedPoint point) => $"{point.Instrument} ({TagsOf(point)})";

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

    private static List<ExportedPoint> Read(IEnumerable<Metric> metrics)
    {
        List<ExportedPoint> points = [];
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

                points.Add(new ExportedPoint(
                    metric.MeterName,
                    new CapturedPoint(
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
                        tags)));
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
