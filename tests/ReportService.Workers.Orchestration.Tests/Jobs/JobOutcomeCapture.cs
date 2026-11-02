using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The measurements one unit of work produced: the capture is opened for a job
/// identifier, enables every instrument this process publishes on the product's meter,
/// keeps the measurements whose recording ran inside that identifier and drops the
/// rest — a measurement recorded under another unit belongs to that unit's own capture,
/// and one recorded outside any unit belongs to no capture at all.
/// <para>
/// The callback runs on the thread that recorded the measurement, so the unit of work
/// it reads is the one the measurement belongs to. A read returns a snapshot, so a case
/// can never enumerate a collection a recording thread is writing; the identifier is
/// never a dimension of an instrument, so the capture is the only place it is visible.
/// </para>
/// </summary>
internal sealed class JobOutcomeCapture : IDisposable
{
    private readonly string _reportJobId;
    private readonly MeterListener _listener = new();
    private readonly Lock _gate = new();
    private readonly List<Measurement> _captured = [];

    /// <summary>Opens a capture for the unit of work of <paramref name="reportJobId"/>.</summary>
    /// <param name="reportJobId">The identifier the product opens the unit of work with.</param>
    public JobOutcomeCapture(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        _reportJobId = reportJobId;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, TelemetryDefaults.MeterName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.Start();
    }

    /// <summary>The measurements of this unit kept so far, as a value no other thread can change.</summary>
    public IReadOnlyList<Measurement> Captured
    {
        get
        {
            lock (_gate)
            {
                return [.. _captured];
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!string.Equals(JobContext.Current, _reportJobId, StringComparison.Ordinal))
        {
            return;
        }

        var measurement = new Measurement(instrument.Name, value, tags.ToArray());
        lock (_gate)
        {
            _captured.Add(measurement);
        }
    }

    /// <summary>One measurement an instrument received, with the tags it carried.</summary>
    /// <param name="Name">The instrument's name.</param>
    /// <param name="Value">The value it received.</param>
    /// <param name="Tags">The tags it carried.</param>
    public sealed record Measurement(string Name, long Value, KeyValuePair<string, object?>[] Tags)
    {
        /// <summary>The value of the tag <paramref name="name"/>, or <see langword="null"/> when it carries none.</summary>
        public object? Tag(string name) =>
            Tags.Where(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Select(tag => tag.Value).FirstOrDefault();
    }
}
