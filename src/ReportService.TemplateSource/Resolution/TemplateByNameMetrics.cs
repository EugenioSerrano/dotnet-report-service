using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.TemplateSource.Resolution;

/// <summary>
/// The instruments of the by-name path: how many resolutions ended each way, and
/// how many candidates one considered and examined. Every tag is a constant from
/// a fixed set — an outcome, a code — so a metric never carries a template, a
/// version, a job or any other value a caller sent.
/// </summary>
public sealed class TemplateByNameMetrics : IDisposable
{
    /// <summary>The counter of resolutions, by outcome and code.</summary>
    public const string ResolutionsInstrumentName = "report_service.template.by_name.resolutions";

    /// <summary>The histogram of how many candidates one resolution considered and examined.</summary>
    public const string CandidatesInstrumentName = "report_service.template.by_name.candidates";

    /// <summary>The outcome tag of a resolution that chose a version by taking it.</summary>
    public const string ResolvedOutcome = "resolved";

    /// <summary>The outcome tag of a resolution that used a version already taken because the source could not be checked.</summary>
    public const string ResolvedFromTakenCopyOutcome = "resolved_from_taken_copy";

    /// <summary>The outcome tag of a resolution that refused the template.</summary>
    public const string RefusedOutcome = "refused";

    /// <summary>The code tag of an outcome that carries no error code.</summary>
    public const string NoCode = "none";

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _resolutions;
    private readonly Histogram<int> _candidates;

    /// <summary>Creates the instruments of the by-name path.</summary>
    public TemplateByNameMetrics()
    {
        _resolutions = _meter.CreateCounter<long>(
            ResolutionsInstrumentName,
            unit: "{resolution}",
            description: "By-name template resolutions, by outcome and code.");
        _candidates = _meter.CreateHistogram<int>(
            CandidatesInstrumentName,
            unit: "{candidate}",
            description: "Candidates one by-name resolution considered and examined.");
    }

    /// <summary>Records one resolution.</summary>
    /// <param name="outcome">A fixed outcome: resolved, resolved_from_taken_copy or refused.</param>
    /// <param name="code">A fixed code, or none when the resolution chose a version.</param>
    public void Resolution(string outcome, string code) =>
        _resolutions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("code", code));

    /// <summary>Records how many candidates one resolution considered and examined.</summary>
    /// <param name="considered">How many candidates survived every exclusion.</param>
    /// <param name="examined">How many of them the walk examined.</param>
    public void Candidates(int considered, int examined)
    {
        _candidates.Record(considered, new KeyValuePair<string, object?>("kind", "considered"));
        _candidates.Record(examined, new KeyValuePair<string, object?>("kind", "examined"));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
