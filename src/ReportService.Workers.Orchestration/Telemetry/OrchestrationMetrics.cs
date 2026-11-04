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

    /// <summary>The counter of job outcomes.</summary>
    public const string JobOutcomesInstrumentName = "reportservice.job.outcomes";

    /// <summary>The counter of the ways a wait for an upload ended.</summary>
    public const string InputWaitOutcomesInstrumentName = "reportservice.job.input.wait.outcomes";

    /// <summary>The histogram of the checks one wait performed.</summary>
    public const string InputWaitPassesInstrumentName = "reportservice.job.input.wait.passes";

    /// <summary>The counter of the notifications sent to waiting jobs.</summary>
    public const string InputWaitNotificationsInstrumentName = "reportservice.job.input.wait.notifications";

    /// <summary>The histogram of how long a job waited between acceptance and its first stage.</summary>
    public const string QueueToFirstStageDurationInstrumentName = "reportservice.job.queue_to_first_stage.duration";

    /// <summary>The counter of a job's template resolutions, by form, path and code.</summary>
    public const string TemplateResolutionsInstrumentName = "reportservice.template.resolutions";

    /// <summary>The status tag of a job that ended failed.</summary>
    public const string FailedJobStatus = "FAILED";

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

    /// <summary>The result tag of a notification that reached its execution.</summary>
    public const string SignalledResult = "signalled";

    /// <summary>The result tag of a notification whose execution does not exist or has closed.</summary>
    public const string SkippedResult = "skipped";

    /// <summary>The form tag of a reference that named one version.</summary>
    public const string TemplateFormExact = "exact";

    /// <summary>The form tag of a reference that named only the template.</summary>
    public const string TemplateFormByName = "by-name";

    private readonly Meter _meter;
    private readonly Counter<long> _outcomes;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _jobOutcomes;
    private readonly Counter<long> _inputWaitOutcomes;
    private readonly Counter<long> _inputWaitNotifications;
    private readonly Histogram<double> _queueToFirstStageDuration;
    private readonly Counter<long> _templateResolutions;
    private readonly Dictionary<string, double> _pendingAge = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates the host's instruments on the identity it carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public OrchestrationMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
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
        _jobOutcomes = _meter.CreateCounter<long>(
            JobOutcomesInstrumentName,
            unit: "{job}",
            description: "Report jobs that ended, by status and code.");
        _inputWaitOutcomes = _meter.CreateCounter<long>(
            InputWaitOutcomesInstrumentName,
            unit: "{wait}",
            description: "Jobs whose wait for their upload ended, by outcome.");
        _inputWaitNotifications = _meter.CreateCounter<long>(
            InputWaitNotificationsInstrumentName,
            unit: "{notification}",
            description: "Notifications of waiting jobs, by result.");
        _queueToFirstStageDuration = _meter.CreateHistogram<double>(
            QueueToFirstStageDurationInstrumentName,
            unit: "s",
            description: "How long a job waited between its acceptance and its first stage.");
        _templateResolutions = _meter.CreateCounter<long>(
            TemplateResolutionsInstrumentName,
            unit: "{resolution}",
            description: "Resolutions of a job's template, by form, path and code.");
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

    /// <summary>Records one job that ended.</summary>
    /// <param name="status">The status the job ended with, a fixed value.</param>
    /// <param name="code">The code it ended with, a fixed value.</param>
    public void JobOutcome(string status, string code) =>
        _jobOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("code", code));

    /// <summary>Records that one job's wait for its upload ended with <paramref name="outcome"/>.</summary>
    /// <param name="outcome">A fixed outcome: ready, rejected or expired.</param>
    public void InputWaitOutcome(string outcome) =>
        _inputWaitOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records one notification of a waiting job.</summary>
    /// <param name="result">A fixed result: signalled, skipped or failed.</param>
    public void InputWaitNotification(string result) =>
        _inputWaitNotifications.Add(
            1,
            new KeyValuePair<string, object?>("result", result));

    /// <summary>Records how long a job waited between its acceptance and its first stage.</summary>
    /// <param name="elapsed">The elapsed time; it is recorded with no tag at all, so it can never carry an identifier.</param>
    public void RecordQueueToFirstStageDuration(TimeSpan elapsed) =>
        _queueToFirstStageDuration.Record(elapsed.TotalSeconds);

    /// <summary>Records how one job's template was fixed, or which code failed it.</summary>
    /// <param name="form">A fixed form: exact or by-name.</param>
    /// <param name="path">A fixed path: taken, took, baseline or failed.</param>
    /// <param name="code">A fixed code, or none when the job was fixed.</param>
    public void TemplateResolution(string form, string path, string code) =>
        _templateResolutions.Add(
            1,
            new KeyValuePair<string, object?>("form", form),
            new KeyValuePair<string, object?>("path", path),
            new KeyValuePair<string, object?>("code", code));

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
