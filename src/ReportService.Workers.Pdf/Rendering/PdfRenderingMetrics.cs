using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

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

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
