using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Telemetry;

/// <summary>
/// The API's instrument for the decision that turns a request into work: how often
/// a job was accepted and how often one was refused, by code. Every tag is a
/// constant from a fixed set, so a metric never carries a declared identity, a job
/// or upload identifier, or any other value a caller sent.
/// </summary>
public sealed class JobAcceptanceMetrics : IDisposable
{
    /// <summary>The counter of job acceptances and refusals.</summary>
    public const string InstrumentName = "reportservice.job.acceptances";

    /// <summary>The result of a request whose job was committed.</summary>
    public const string AcceptedResult = "accepted";

    /// <summary>The result of a request that was refused and stored nothing.</summary>
    public const string RefusedResult = "refused";

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _acceptances;

    /// <summary>Creates the API's job instruments.</summary>
    public JobAcceptanceMetrics() =>
        _acceptances = _meter.CreateCounter<long>(
            InstrumentName,
            unit: "{request}",
            description: "Report job requests, by result and, for a refusal, by code.");

    /// <summary>Records one accepted job.</summary>
    public void Accepted() =>
        _acceptances.Add(1, new KeyValuePair<string, object?>("result", AcceptedResult));

    /// <summary>Records one refused request.</summary>
    /// <param name="code">The refusal's code, a fixed value.</param>
    public void Refusal(string code) =>
        _acceptances.Add(
            1,
            new KeyValuePair<string, object?>("result", RefusedResult),
            new KeyValuePair<string, object?>("code", code));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
