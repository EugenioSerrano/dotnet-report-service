namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The telemetry tests run one at a time: the diagnostics endpoint reads and
/// writes a process-wide environment entry, so two classes must not drive it at
/// once. A host's meter identity and its unit-of-work log scope are its own, so
/// the serialisation is not what keeps one host's telemetry out of another's.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TelemetryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "telemetry";
}
