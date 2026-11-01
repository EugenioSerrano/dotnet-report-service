using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The development-only capture: an exporter that hands every metric point the
/// pipeline exports to the <see cref="TelemetryCaptureBuffer"/>, so the read
/// endpoint can answer with what this process measured. It is a pure observer —
/// it changes no name, unit, type, value, tag or time, and it adds no tag — and
/// it neither blocks nor filters. It is attached to its own reader, so it
/// receives points with or without a configured destination.
/// </summary>
internal sealed class TelemetryCaptureExporter(TelemetryCaptureBuffer buffer) : BaseExporter<Metric>
{
    /// <inheritdoc />
    public override ExportResult Export(in Batch<Metric> batch)
    {
        if (ParentProvider is { } provider)
        {
            buffer.SetResource(Attributes(ProviderExtensions.GetResource(provider).Attributes));
        }

        List<CapturedInstrument> declared = [];
        List<CapturedPoint> captured = [];
        foreach (var metric in batch)
        {
            var type = TypeName(metric.MetricType);
            var unit = metric.Unit ?? string.Empty;
            declared.Add(new CapturedInstrument(metric.Name, metric.MeterName, type, unit));

            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                captured.Add(Capture(metric, point, type, unit));
            }
        }

        buffer.Record(declared, captured);
        return ExportResult.Success;
    }

    private static CapturedPoint Capture(Metric metric, in MetricPoint point, string type, string unit)
    {
        double? value = null;
        bool? monotonic = null;
        long? count = null;
        double? sum = null;
        double? min = null;
        double? max = null;

        if (metric.MetricType.IsGauge())
        {
            value = metric.MetricType.IsDouble()
                ? point.GetGaugeLastValueDouble()
                : point.GetGaugeLastValueLong();
        }
        else if (metric.MetricType.IsSum())
        {
            value = metric.MetricType.IsDouble()
                ? point.GetSumDouble()
                : point.GetSumLong();
            monotonic = metric.MetricType is MetricType.LongSum or MetricType.DoubleSum;
        }
        else if (metric.MetricType.IsHistogram())
        {
            count = point.GetHistogramCount();
            sum = point.GetHistogramSum();
            if (point.TryGetHistogramMinMaxValues(out var lowest, out var highest))
            {
                min = lowest;
                max = highest;
            }
        }

        return new CapturedPoint(
            metric.Name,
            type,
            unit,
            value,
            monotonic,
            count,
            sum,
            min,
            max,
            point.StartTime.ToUniversalTime(),
            point.EndTime.ToUniversalTime(), Tags(point.Tags));
    }

    private static IReadOnlyList<KeyValuePair<string, string?>> Tags(ReadOnlyTagCollection tags)
    {
        List<KeyValuePair<string, string?>> captured = new(tags.Count);
        foreach (var tag in tags)
        {
            captured.Add(new KeyValuePair<string, string?>(
                tag.Key,
                Convert.ToString(tag.Value, CultureInfo.InvariantCulture)));
        }

        return captured;
    }

    private static IReadOnlyDictionary<string, string?> Attributes(IEnumerable<KeyValuePair<string, object>> attributes)
    {
        var captured = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            captured[attribute.Key] = Convert.ToString(attribute.Value, CultureInfo.InvariantCulture);
        }

        return captured;
    }

    private static string TypeName(MetricType type) => type switch
    {
        MetricType.LongGauge or MetricType.DoubleGauge => "Gauge",
        MetricType.LongSum or MetricType.DoubleSum or MetricType.LongSumNonMonotonic or MetricType.DoubleSumNonMonotonic => "Sum",
        MetricType.Histogram or MetricType.ExponentialHistogram => "Histogram",
        _ => type.ToString(),
    };
}
