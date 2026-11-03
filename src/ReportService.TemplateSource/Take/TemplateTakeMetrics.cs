using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.TemplateSource.Take;

/// <summary>
/// The take's instruments: how many takes ended each way, how long one took and
/// how many requests the source received of each kind. Every tag is a fixed
/// value — an outcome, a code, a request kind, a status class — and never a
/// template identifier, a version, a key, a hash or a size, so a metric can
/// neither carry template content nor multiply its own series.
/// </summary>
public sealed class TemplateTakeMetrics : IDisposable
{
    /// <summary>The counter of takes, by outcome and code.</summary>
    public const string TakesInstrumentName = "reportservice.template.takes";

    /// <summary>The histogram of how long one take took.</summary>
    public const string DurationInstrumentName = "reportservice.template.take.duration";

    /// <summary>The counter of requests the take made to the source.</summary>
    public const string SourceRequestsInstrumentName = "reportservice.template.source.requests";

    /// <summary>The request kind of the commit the read is pinned to.</summary>
    public const string CommitRequest = "commit";

    /// <summary>The request kind of a tree read.</summary>
    public const string TreeRequest = "tree";

    /// <summary>The request kind of one file's bytes.</summary>
    public const string BlobRequest = "blob";

    /// <summary>The status class of a request that answered.</summary>
    public const string OkStatus = "ok";

    /// <summary>The status class of a request that failed.</summary>
    public const string ErrorStatus = "error";

    /// <summary>The code tag of an answer that carries none.</summary>
    public const string NoCode = "none";

    private readonly Meter _meter = new(TelemetryDefaults.MeterName);
    private readonly Counter<long> _takes;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _requests;

    /// <summary>Creates the take's instruments.</summary>
    public TemplateTakeMetrics()
    {
        _takes = _meter.CreateCounter<long>(
            TakesInstrumentName,
            unit: "{take}",
            description: "Template versions taken, by outcome and code.");
        _duration = _meter.CreateHistogram<double>(
            DurationInstrumentName,
            unit: "ms",
            description: "How long one take took, by outcome.");
        _requests = _meter.CreateCounter<long>(
            SourceRequestsInstrumentName,
            unit: "{request}",
            description: "Requests the take made to the template source, by kind and status class.");
    }

    /// <summary>Records one take that ended with <paramref name="outcome"/> and <paramref name="code"/>.</summary>
    /// <param name="outcome">A fixed outcome: taken, already_taken, conflict_logged or refused.</param>
    /// <param name="code">A fixed code, or none when the answer carries none.</param>
    /// <param name="elapsed">How long the take took.</param>
    public void TakeEnded(string outcome, string code, TimeSpan elapsed)
    {
        _takes.Add(
            1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("code", code));
        _duration.Record(
            elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    /// <summary>Records one request the source received.</summary>
    /// <param name="kind">A fixed kind: commit, tree or blob.</param>
    /// <param name="statusClass">A fixed status class: ok or error.</param>
    public void SourceRequest(string kind, string statusClass) =>
        _requests.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("status_class", statusClass));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
