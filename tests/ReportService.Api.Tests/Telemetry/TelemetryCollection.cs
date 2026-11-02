namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The telemetry tests run one at a time: the service's unit-of-work log scope is bound
/// to one process-wide logger factory and the last host to start replaces it, so two
/// telemetry hosts open at once would interleave the scoped entries they write. A
/// capture's correctness does not depend on this collection: each one holds the
/// telemetry of the unit of work its own case opened, whichever cases run beside it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TelemetryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "telemetry";
}
