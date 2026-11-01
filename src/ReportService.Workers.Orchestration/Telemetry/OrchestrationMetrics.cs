using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Workers.Orchestration.Telemetry;

/// <summary>
/// The instruments of the work this process does: how many uploads ended each
/// way, and how long verifying one took. Every tag is a constant value from a
/// fixed set — an outcome, a code, a stage — and never an identifier, a key or a
/// size, so a metric cannot carry a consumer's data or multiply its own series.
/// </summary>
public sealed class OrchestrationMetrics : IDisposable
{
    /// <summary>The counter of upload outcomes.</summary>
    public const string OutcomesInstrumentName = "reportservice.upload.outcomes";

    /// <summary>The histogram of how long verifying one upload took.</summary>
    public const string VerificationDurationInstrumentName = "reportservice.upload.verification.duration";

    /// <summary>The stages an outcome can belong to.</summary>
    public const string TransferStage = "transfer";

    /// <summary>The stage a verification outcome belongs to.</summary>
    public const string VerificationStage = "verification";

    /// <summary>The outcome tag of an upload that ended ready.</summary>
    public const string ReadyOutcome = "ready";

    /// <summary>The outcome tag of an upload that ended rejected.</summary>
    public const string RejectedOutcome = "rejected";

    /// <summary>The code tag of an upload that ended ready.</summary>
    public const string NoCode = "none";

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the process's instruments.</summary>
    public OrchestrationMetrics()
    {
        _outcomes = _meter.CreateCounter<long>(
            OutcomesInstrumentName,
            unit: "{upload}",
            description: "Uploads that ended, by outcome, code and stage.");
        _duration = _meter.CreateHistogram<double>(
            VerificationDurationInstrumentName,
            unit: "s",
            description: "How long verifying one upload took, by outcome.");
    }

    /// <summary>Records that one upload ended with <paramref name="outcome"/> and <paramref name="code"/>.</summary>
    /// <param name="outcome">A fixed outcome: ready or rejected.</param>
    /// <param name="code">A fixed code, or none when the upload is ready.</param>
    /// <param name="stage">A fixed stage: transfer or verification.</param>
    public void UploadOutcome(string outcome, string code, string stage) =>
        _outcomes.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("stage", stage));

    /// <summary>Records how long one verification took and how it ended.</summary>
    /// <param name="outcome">A fixed outcome: ready or rejected.</param>
    /// <param name="elapsed">How long the verification took.</param>
    public void RecordVerificationDuration(string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
