using System.Diagnostics.Metrics;
using ReportService.Api.Telemetry;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Tests.Artifacts;

/// <summary>
/// Listens to the two instruments a download counts on, so a case can say how many
/// transfers were cut and how many stored objects could not be read. Every host builds
/// its own meter of the same name, so the listener enables whatever is published after it
/// started: a case starts listening before its host handles a request, and reads the
/// difference around the one request it made. Nothing else in the process counts on these
/// instruments, so a measurement is always the case's own.
/// </summary>
internal sealed class DownloadInstruments : IDisposable
{
    private readonly MeterListener _listener;
    private long _integrityFailures;
    private long _missingObjects;

    /// <summary>Starts listening, before the host that counts is asked to serve anything.</summary>
    public DownloadInstruments()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, TelemetryDefaults.MeterName, StringComparison.Ordinal)
                    && instrument.Name is ArtifactDownloadMetrics.IntegrityFailuresInstrumentName
                        or ArtifactDownloadMetrics.MissingObjectsInstrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.Start();
    }

    /// <summary>How many transfers were counted as cut since the counts were last reset.</summary>
    public long IntegrityFailures => Interlocked.Read(ref _integrityFailures);

    /// <summary>How many stored objects were counted as unreadable since the counts were last reset.</summary>
    public long MissingObjects => Interlocked.Read(ref _missingObjects);

    /// <summary>Sets both counts back to zero.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _integrityFailures, 0);
        Interlocked.Exchange(ref _missingObjects, 0);
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (string.Equals(instrument.Name, ArtifactDownloadMetrics.IntegrityFailuresInstrumentName, StringComparison.Ordinal))
        {
            Interlocked.Add(ref _integrityFailures, measurement);
            return;
        }

        if (string.Equals(instrument.Name, ArtifactDownloadMetrics.MissingObjectsInstrumentName, StringComparison.Ordinal))
        {
            Interlocked.Add(ref _missingObjects, measurement);
        }
    }
}
