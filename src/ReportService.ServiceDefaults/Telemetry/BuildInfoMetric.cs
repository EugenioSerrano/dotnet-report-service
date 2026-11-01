using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// The product's own meter and its single build-info instrument:
/// <c>service.build_info</c>, a gauge of value 1 whose tags name the build and
/// the runtime the process reports. The tags are constants held by this type —
/// a measurement never takes a tag from a caller — so no per-job dimension can
/// be added to it. Further instruments of the same meter arrive with the work
/// they measure and follow the same rule.
/// </summary>
internal sealed class BuildInfoMetric : IDisposable
{
    /// <summary>The name of the meter every product instrument is created on.</summary>
    public const string MeterName = "ReportService";

    /// <summary>The name of the build-info instrument.</summary>
    public const string InstrumentName = "service.build_info";

    /// <summary>The instrument's tags: the build's version and the runtime.</summary>
    public static readonly KeyValuePair<string, object?>[] Tags =
    [
        new("version", ProductVersion.Informational),
        new("runtime", RuntimeInformation.FrameworkDescription),
    ];

    private readonly Meter _meter = new(MeterName);
    private readonly Gauge<double> _buildInfo;

    /// <summary>Creates the meter and its instrument.</summary>
    public BuildInfoMetric() =>
        _buildInfo = _meter.CreateGauge<double>(
            InstrumentName,
            unit: null,
            description: "Always 1; the tags name the build and the runtime.");

    /// <summary>Records one measurement of the build-info gauge with its constant tags.</summary>
    public void Record() => _buildInfo.Record(1, Tags);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
