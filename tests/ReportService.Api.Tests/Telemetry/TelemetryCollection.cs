namespace ReportService.Api.Tests.Telemetry;

/// <summary>
/// The telemetry tests run one at a time: a unit of work is opened on a static
/// scope and every host registers its logger factory there, so two hosts must
/// never be open at once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TelemetryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "telemetry";
}
