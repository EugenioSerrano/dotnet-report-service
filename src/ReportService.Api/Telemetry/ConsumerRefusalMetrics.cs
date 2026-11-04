using System.Diagnostics.Metrics;
using ReportService.ServiceDefaults.Telemetry;

namespace ReportService.Api.Telemetry;

/// <summary>
/// The API's own instruments for the one decision it takes before it looks at
/// anything else: how often a request was refused at the consumer gate, by code,
/// operation and identity state. Every tag is a constant from a fixed set, so a
/// metric can never carry a declared identity, an upload identifier or any other
/// value a caller sent.
/// </summary>
public sealed class ConsumerRefusalMetrics : IDisposable
{
    /// <summary>The counter of refused requests.</summary>
    public const string InstrumentName = "reportservice.consumer.refusals";

    /// <summary>The operation that opens an upload.</summary>
    public const string CreateOperation = "create";

    /// <summary>The operation that reads an upload's status.</summary>
    public const string StatusOperation = "status";

    /// <summary>The operation that receives an upload's content.</summary>
    public const string ContentOperation = "content";

    /// <summary>The operation that confirms an upload.</summary>
    public const string ConfirmOperation = "confirm";

    /// <summary>The operation that reads a taken template version's metadata.</summary>
    public const string TemplateVersionOperation = "template-version";

    /// <summary>The operation that reads one report job's status.</summary>
    public const string JobStatusOperation = "job-status";

    /// <summary>Every operation the gate protects.</summary>
    public static readonly IReadOnlyList<string> Operations =
        [CreateOperation, StatusOperation, ContentOperation, ConfirmOperation, TemplateVersionOperation, JobStatusOperation];

    private readonly Meter _meter;
    private readonly Counter<long> _refusals;

    /// <summary>Creates the API's instruments on the identity the host carries.</summary>
    /// <param name="identity">The identity of the host that owns the instruments.</param>
    public ConsumerRefusalMetrics(MeterIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _meter = new Meter(identity.Name);
        _refusals = _meter.CreateCounter<long>(
            InstrumentName,
            unit: "{request}",
            description: "Requests refused at the consumer gate, by code, operation and identity state.");
    }

    /// <summary>Records one refused request.</summary>
    /// <param name="code">The refusal's code, a fixed value.</param>
    /// <param name="operation">The operation, a fixed value.</param>
    /// <param name="identityState">The state of the declared identity, a fixed value.</param>
    public void Refusal(string code, string operation, string identityState) =>
        _refusals.Add(
            1,
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("identity_state", identityState));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
