using System.Diagnostics.Metrics;
using ReportService.Api.Telemetry;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Tests.Jobs;

/// <summary>
/// Listens to the refusal counter, so a case can say how many requests the gate refused
/// and with which operation and identity state. Every host builds its own meter of the
/// same name, so the listener enables whatever is published after it started: a case
/// starts listening before its host serves a request and reads the measurements of the
/// request it made. Nothing else in the process counts on this instrument.
/// </summary>
internal sealed class RefusalInstruments : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    /// <summary>Starts listening, before the host that counts is asked to serve anything.</summary>
    /// <param name="meterName">The meter the case's own host publishes its instruments on.</param>
    public RefusalInstruments(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, ConsumerRefusalMetrics.InstrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.Start();
    }

    /// <summary>Every measurement recorded since the listener started, in order.</summary>
    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>The number of measurements recorded since the listener started.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _measurements.Count;
            }
        }
    }

    /// <summary>Forgets every measurement, so the next request's measurement is the case's own.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _measurements.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Record(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (!string.Equals(instrument.Name, ConsumerRefusalMetrics.InstrumentName, StringComparison.Ordinal))
        {
            return;
        }

        var entry = new Measurement(Tag(tags, "code"), Tag(tags, "operation"), Tag(tags, "identity_state"));
        lock (_gate)
        {
            for (var count = 0; count < measurement; count++)
            {
                _measurements.Add(entry);
            }
        }
    }

    private static string Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
            {
                return Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>One refusal as the counter recorded it: the three tags, each a constant value.</summary>
    /// <param name="Code">The refusal's code.</param>
    /// <param name="Operation">The operation's fixed value.</param>
    /// <param name="IdentityState">The declared identity's state, as the gate assigned it.</param>
    public sealed record Measurement(string Code, string Operation, string IdentityState);
}
