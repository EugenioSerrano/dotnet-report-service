using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults;

namespace ReportService.Api.Telemetry;

/// <summary>
/// The API's instruments for a download that could not be served from the bytes
/// it had: how often the stored object failed verification while it was being
/// copied, and how often it was missing or unreadable. Both are counters with no
/// tags at all, so a metric can never carry an artifact or job identifier, a key
/// or a path.
/// </summary>
public sealed class ArtifactDownloadMetrics : IDisposable
{
    /// <summary>The counter of transfers cut because the stored object failed verification.</summary>
    public const string IntegrityFailuresInstrumentName = "reportservice.artifact.download.integrity_failures";

    /// <summary>The counter of stored objects the service could not read.</summary>
    public const string MissingObjectsInstrumentName = "reportservice.artifact.download.missing_objects";

    private readonly Meter _meter;
    private readonly Counter<long> _integrityFailures;
    private readonly Counter<long> _missingObjects;

    /// <summary>Creates the API's download instruments on the identity the host carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public ArtifactDownloadMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
        _integrityFailures = _meter.CreateCounter<long>(
            IntegrityFailuresInstrumentName,
            unit: "{download}",
            description: "Downloads whose stored object failed verification and whose transfer was cut.");
        _missingObjects = _meter.CreateCounter<long>(
            MissingObjectsInstrumentName,
            unit: "{download}",
            description: "Downloads whose stored object was missing or could not be read.");
    }

    /// <summary>Records one transfer cut because the stored object failed verification.</summary>
    public void IntegrityFailure() => _integrityFailures.Add(1);

    /// <summary>Records one stored object the service could not read.</summary>
    public void MissingObject() => _missingObjects.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
