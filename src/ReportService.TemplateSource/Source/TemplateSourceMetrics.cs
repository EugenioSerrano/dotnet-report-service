using System.Diagnostics;
using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.TemplateSource.Source;

/// <summary>
/// The source read's instruments and span: how many exact reads ended in each
/// state, how long one took, and how many objects were touched of each kind.
/// Every tag is a fixed value - a state, a kind, a status class - and never a
/// template, a version, a key, a hash or a size, so a metric can neither carry
/// template content nor multiply its own series.
/// </summary>
public sealed class TemplateSourceMetrics : IDisposable
{
    /// <summary>The counter of exact reads, by state.</summary>
    public const string ReadsInstrumentName = "reportservice.template.source.reads";

    /// <summary>The histogram of how long one exact read took.</summary>
    public const string DurationInstrumentName = "reportservice.template.source.read.duration";

    /// <summary>The counter of objects the read touched, by kind and status class.</summary>
    public const string ObjectsInstrumentName = "reportservice.template.source.objects";

    /// <summary>The kind of one publication record read.</summary>
    public const string DescriptorKind = "descriptor";

    /// <summary>The kind of one package file read.</summary>
    public const string FileKind = "file";

    /// <summary>The status class of an object read that succeeded.</summary>
    public const string OkStatus = "ok";

    /// <summary>The status class of an object read that failed.</summary>
    public const string ErrorStatus = "error";

    /// <summary>The fixed state names one exact read can end with.</summary>
    public static class States
    {
        /// <summary>A publication was found and verified.</summary>
        public const string Found = "found";

        /// <summary>Neither a record nor a file exists.</summary>
        public const string Absent = "absent";

        /// <summary>Files exist and no record does.</summary>
        public const string Incomplete = "incomplete";

        /// <summary>More than one record exists.</summary>
        public const string Duplicate = "duplicate";

        /// <summary>The class could not be listed or read.</summary>
        public const string Unreachable = "unreachable";

        /// <summary>The record is invalid or its contract is not read.</summary>
        public const string Refused = "refused";
    }

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _reads;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _objects;

    /// <summary>Creates the source read's instruments.</summary>
    public TemplateSourceMetrics()
    {
        _reads = _meter.CreateCounter<long>(
            ReadsInstrumentName,
            unit: "{read}",
            description: "Exact template-source version reads, by state.");
        _duration = _meter.CreateHistogram<double>(
            DurationInstrumentName,
            unit: "ms",
            description: "How long one exact template-source version read took, by state.");
        _objects = _meter.CreateCounter<long>(
            ObjectsInstrumentName,
            unit: "{object}",
            description: "Objects the template-source read touched, by kind and status class.");
    }

    /// <summary>The one activity source the read starts its spans from.</summary>
    public static ActivitySource ActivitySource { get; } = new(TelemetryDefaults.ActivitySourceName);

    /// <summary>Records one exact read.</summary>
    /// <param name="state">A fixed state name.</param>
    /// <param name="elapsed">How long the read took.</param>
    public void Read(string state, TimeSpan elapsed)
    {
        _reads.Add(1, new KeyValuePair<string, object?>("state", state));
        _duration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("state", state));
    }

    /// <summary>Records one object the read touched.</summary>
    /// <param name="kind">A fixed kind: descriptor or file.</param>
    /// <param name="statusClass">A fixed status class: ok or error.</param>
    public void Object(string kind, string statusClass) =>
        _objects.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("status_class", statusClass));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
