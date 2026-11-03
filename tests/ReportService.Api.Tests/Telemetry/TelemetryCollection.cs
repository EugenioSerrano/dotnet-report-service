namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The telemetry tests run one at a time: the diagnostics endpoint reads and writes a
/// process-wide environment entry, and a host's configuration reads the environment at
/// start-up, so two classes must not drive it at once. A host's meter identity and its
/// unit-of-work log scope are its own, so the serialisation is not what keeps one host's
/// telemetry out of another's: a capture holds the telemetry of the unit of work its own
/// case opened, whichever cases run beside it. The class that starts two hosts at once to
/// prove two captures stay apart carries the collection for the same process-wide entry.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TelemetryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "telemetry";
}
