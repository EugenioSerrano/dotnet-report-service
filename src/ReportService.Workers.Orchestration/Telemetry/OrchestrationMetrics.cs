using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Workers.Orchestration.Telemetry;

/// <summary>
/// The instruments of the work this process does: how many uploads ended each
/// way, how long verifying one took, how often a durable request was dispatched
/// and how long the oldest one has been waiting. Every tag is a constant value from
/// a fixed set — an outcome, a code, a stage, a kind, a result — and never an
/// identifier, a key or a size, so a metric cannot carry a consumer's data or
/// multiply its own series.
/// </summary>
public sealed class OrchestrationMetrics : IDisposable
{
    /// <summary>The counter of upload outcomes.</summary>
    public const string OutcomesInstrumentName = "reportservice.upload.outcomes";

    /// <summary>The histogram of how long verifying one upload took.</summary>
    public const string VerificationDurationInstrumentName = "reportservice.upload.verification.duration";

    /// <summary>The counter of dispatch attempts.</summary>
    public const string DispatchAttemptsInstrumentName = "reportservice.intent.dispatch.attempts";

    /// <summary>The gauge of how long the oldest pending request has waited.</summary>
    public const string PendingAgeInstrumentName = "reportservice.intent.pending.age";

    /// <summary>The stages an outcome can belong to.</summary>
    public const string TransferStage = "transfer";

    /// <summary>The stage a verification outcome belongs to.</summary>
    public const string VerificationStage = "verification";

    /// <summary>The outcome tag of an upload that ended ready.</summary>
    public const string ReadyOutcome = "ready";

    /// <summary>The outcome tag of an upload that ended rejected.</summary>
    public const string RejectedOutcome = "rejected";

    /// <summary>The outcome tag of an upload that expired before it ended any other way.</summary>
    public const string ExpiredOutcome = "expired";

    /// <summary>The code tag of an upload that ended ready.</summary>
    public const string NoCode = "none";

    /// <summary>The result tag of a request that was dispatched.</summary>
    public const string DispatchedResult = "dispatched";

    /// <summary>The result tag of a dispatch attempt that failed.</summary>
    public const string FailedResult = "failed";

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _attempts;
    private readonly Dictionary<string, double> _pendingAge = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

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
        _attempts = _meter.CreateCounter<long>(
            DispatchAttemptsInstrumentName,
            unit: "{attempt}",
            description: "Attempts to dispatch a durable request, by kind and result.");
        _meter.CreateObservableGauge(
            PendingAgeInstrumentName,
            ObservePendingAge,
            unit: "s",
            description: "How long the oldest pending durable request of each kind has waited.");
    }

    /// <summary>Records that one upload ended with <paramref name="outcome"/> and <paramref name="code"/>.</summary>
    /// <param name="outcome">A fixed outcome: ready, rejected or expired.</param>
    /// <param name="code">A fixed code, or none when the upload is ready.</param>
    /// <param name="stage">A fixed stage: transfer, verification, session or verification-deadline.</param>
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

    /// <summary>Records one attempt to dispatch a request.</summary>
    /// <param name="kind">The request's kind, a fixed value.</param>
    /// <param name="result">A fixed result: dispatched or failed.</param>
    public void DispatchAttempt(string kind, string result) =>
        _attempts.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("result", result));

    /// <summary>Publishes how long the oldest pending request of <paramref name="kind"/> has waited.</summary>
    /// <param name="kind">The request's kind, a fixed value.</param>
    /// <param name="seconds">The age in seconds; zero when nothing waits.</param>
    public void ObservePendingAge(string kind, double seconds)
    {
        lock (_gate)
        {
            _pendingAge[kind] = seconds;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<double>> ObservePendingAge()
    {
        KeyValuePair<string, double>[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _pendingAge];
        }

        foreach (var (kind, seconds) in snapshot)
        {
            yield return new Measurement<double>(seconds, new KeyValuePair<string, object?>("kind", kind));
        }
    }
}
