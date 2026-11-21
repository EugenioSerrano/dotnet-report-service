using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Workers.Pdf.Templates;

/// <summary>
/// The instruments of the take's sample render: how long one render took and how it
/// ended, and which rule refused a version. Every tag is a fixed value from a small
/// set - an engine, an outcome, a rule - and never an identifier, a key, a value or
/// a printed text, so a measurement carries neither a consumer's data nor a series
/// per template.
/// </summary>
public sealed class SampleRenderMetrics : IDisposable
{
    /// <summary>The histogram of how long one sample render took, by engine and outcome.</summary>
    public const string RenderInstrumentName = "report_service.template.sample.render";

    /// <summary>The counter of refused samples, by rule and engine.</summary>
    public const string FailuresInstrumentName = "report_service.template.sample.failures";

    /// <summary>The outcome tag of a sample that rendered and passed.</summary>
    public const string PassedOutcome = "pass";

    /// <summary>The outcome tag of a sample that refused the version.</summary>
    public const string RefusedOutcome = "refused";

    /// <summary>The outcome tag of a render that could not run or did not finish.</summary>
    public const string UnavailableOutcome = "unavailable";

    private readonly Meter _meter;
    private readonly Histogram<double> _render;
    private readonly Counter<long> _failures;

    /// <summary>Creates the worker's instruments on the identity it carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public SampleRenderMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
        _render = _meter.CreateHistogram<double>(
            RenderInstrumentName,
            unit: "ms",
            description: "How long one sample render took, by engine and outcome.");
        _failures = _meter.CreateCounter<long>(
            FailuresInstrumentName,
            unit: "{sample}",
            description: "Samples that refused a version, by rule and engine.");
    }

    /// <summary>Records one sample render and how it ended.</summary>
    /// <param name="engine">The engine the version declares, as a manifest names it.</param>
    /// <param name="outcome">One of <see cref="PassedOutcome"/>, <see cref="RefusedOutcome"/>, <see cref="UnavailableOutcome"/>.</param>
    /// <param name="elapsedMs">How long the render took, in milliseconds.</param>
    public void Rendered(string engine, string outcome, double elapsedMs) =>
        _render.Record(
            elapsedMs,
            new KeyValuePair<string, object?>("engine", engine),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records one refusal under its rule.</summary>
    /// <param name="engine">The engine the version declares, as a manifest names it.</param>
    /// <param name="rule">The rule label of the refusal.</param>
    public void Refused(string engine, string rule) =>
        _failures.Add(
            1,
            new KeyValuePair<string, object?>("rule", rule),
            new KeyValuePair<string, object?>("engine", engine));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
