using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults;

namespace ReportService.Workers.Pdf.Rendering;

/// <summary>
/// The instruments of printing one block: how long each phase of a render took, how the
/// verification ended and with which failure class, how many pages a block printed, and how
/// many requests Chromium made that the route policy aborted. Every tag is a fixed value
/// from a small set - a phase, a result, a class - and never an identifier, a key or a
/// document's text, so a measurement can carry neither a consumer's data nor a series per job.
/// </summary>
public sealed class PdfRenderingMetrics : IDisposable
{
    /// <summary>The histogram of how long one phase of a block's render took.</summary>
    public const string BlockDurationInstrumentName = "reportservice.pdf.block.duration";

    /// <summary>The counter of verification outcomes, by result and failure class.</summary>
    public const string VerificationOutcomesInstrumentName = "reportservice.pdf.verification.outcomes";

    /// <summary>The histogram of the pages one printed block holds.</summary>
    public const string PagesInstrumentName = "reportservice.pdf.pages";

    /// <summary>The counter of requests Chromium made that the route policy aborted.</summary>
    public const string AbortedRequestsInstrumentName = "reportservice.pdf.chromium.aborted_requests";

    /// <summary>The counter of activity attempts the engine retried, by queue and cause.</summary>
    public const string ActivityRetriesInstrumentName = "reportservice.activity.retries";

    /// <summary>The counter of renders the watchdog stopped, by reason.</summary>
    public const string WatchdogExitsInstrumentName = "reportservice.pdf.watchdog.exits";

    /// <summary>The counter of causes attempts recorded, by cause.</summary>
    public const string AttemptCausesInstrumentName = "reportservice.pdf.attempt.causes";

    /// <summary>The queue a printed block's activity runs on, as the retry counter tags it.</summary>
    public const string RenderQueue = "report-pdf-rendering";

    /// <summary>The retry's cause tag when the previous attempt's worker was lost.</summary>
    public const string HeartbeatLostCause = "heartbeat-lost";

    /// <summary>The retry's cause tag when the previous attempt ended without a recorded cause.</summary>
    public const string CrashCause = "crash";

    /// <summary>The retry's cause tag when the previous attempt passed its own deadline.</summary>
    public const string DeadlineCause = "deadline";

    /// <summary>The reason tag of a watchdog exit: an attempt passed its deadline and grace.</summary>
    public const string DeadlineExitReason = "deadline";

    /// <summary>The tag of the composition phase.</summary>
    public const string ComposePhase = "compose";

    /// <summary>The tag of the print phase.</summary>
    public const string PrintPhase = "print";

    /// <summary>The tag of the verification phase.</summary>
    public const string VerifyPhase = "verify";

    /// <summary>The tag of the store phase.</summary>
    public const string StorePhase = "store";

    /// <summary>The result tag of a verification that passed.</summary>
    public const string PassResult = "pass";

    /// <summary>The result tag of a verification that failed.</summary>
    public const string FailResult = "fail";

    /// <summary>The class tag of a verification that failed for no named reason.</summary>
    public const string NoClass = "none";

    private readonly Meter _meter;
    private readonly Histogram<double> _blockDuration;
    private readonly Counter<long> _verificationOutcomes;
    private readonly Histogram<long> _pages;
    private readonly Counter<long> _abortedRequests;
    private readonly Counter<long> _activityRetries;
    private readonly Counter<long> _watchdogExits;
    private readonly Counter<long> _attemptCauses;

    /// <summary>Creates the host's instruments on the identity it carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public PdfRenderingMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
        _blockDuration = _meter.CreateHistogram<double>(
            BlockDurationInstrumentName,
            unit: "s",
            description: "How long one phase of a printed block took, by phase.");
        _verificationOutcomes = _meter.CreateCounter<long>(
            VerificationOutcomesInstrumentName,
            unit: "{verification}",
            description: "Printed blocks verified, by result and failure class.");
        _pages = _meter.CreateHistogram<long>(
            PagesInstrumentName,
            unit: "{page}",
            description: "Pages one printed block holds.");
        _abortedRequests = _meter.CreateCounter<long>(
            AbortedRequestsInstrumentName,
            unit: "{request}",
            description: "Requests a printed document made that the route policy aborted.");
        _activityRetries = _meter.CreateCounter<long>(
            ActivityRetriesInstrumentName,
            unit: "{retry}",
            description: "Activity attempts the engine retried, by queue and cause.");
        _watchdogExits = _meter.CreateCounter<long>(
            WatchdogExitsInstrumentName,
            unit: "{exit}",
            description: "Renders the watchdog stopped, by reason.");
        _attemptCauses = _meter.CreateCounter<long>(
            AttemptCausesInstrumentName,
            unit: "{cause}",
            description: "Causes an attempt recorded before it ended, by cause.");
    }

    /// <summary>Records how long one phase of a block's render took.</summary>
    /// <param name="phase">A fixed phase: compose, print, verify or store.</param>
    /// <param name="elapsed">How long the phase took.</param>
    public void BlockPhase(string phase, TimeSpan elapsed) =>
        _blockDuration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("phase", phase));

    /// <summary>Records how one verification ended.</summary>
    /// <param name="passed">Whether it passed.</param>
    /// <param name="failureClass">The failure's reason word, or none when it passed.</param>
    public void VerificationOutcome(bool passed, string? failureClass) =>
        _verificationOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("result", passed ? PassResult : FailResult),
            new KeyValuePair<string, object?>("class", failureClass ?? NoClass));

    /// <summary>Records how many pages one printed block holds.</summary>
    /// <param name="pages">The page count.</param>
    public void Pages(int pages) => _pages.Record(pages);

    /// <summary>Records how many requests one printed document made that were aborted.</summary>
    /// <param name="count">The number of aborted requests.</param>
    public void AbortedRequests(int count)
    {
        if (count > 0)
        {
            _abortedRequests.Add(count);
        }
    }

    /// <summary>Records one activity attempt the engine retried.</summary>
    /// <param name="queue">The queue the activity ran on; a fixed value.</param>
    /// <param name="cause">The cause of the retry: a lost worker, a crash or a deadline.</param>
    public void ActivityRetry(string queue, string cause) =>
        _activityRetries.Add(
            1,
            new KeyValuePair<string, object?>("queue", queue),
            new KeyValuePair<string, object?>("cause", cause));

    /// <summary>Records one render the watchdog stopped.</summary>
    /// <param name="reason">The reason; a fixed value.</param>
    public void WatchdogExit(string reason) =>
        _watchdogExits.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records one cause an attempt recorded before it ended.</summary>
    /// <param name="cause">The deadline class the attempt ended with.</param>
    public void AttemptCause(string cause) =>
        _attemptCauses.Add(1, new KeyValuePair<string, object?>("cause", cause));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
