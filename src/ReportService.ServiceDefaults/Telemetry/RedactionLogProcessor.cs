using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// Removes sensitive content from a log record before it is exported: an
/// attribute whose name matches <see cref="RedactionOptions.DeniedNamePattern"/>
/// is dropped from the record, every configured sensitive value and the storage
/// root path is replaced with <see cref="RedactionOptions.Replacement"/> in the
/// attributes and in the formatted message, and an over-long value is truncated.
/// The scope attributes of a unit of work (<c>job_id</c>) and the fields of a
/// start-up line are kept.
/// </summary>
internal sealed class RedactionLogProcessor(RedactionOptions options) : BaseProcessor<LogRecord>
{
    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        if (data is null)
        {
            return;
        }

        data.FormattedMessage = options.Redact(data.FormattedMessage);
        if (data.Attributes is not { Count: > 0 } attributes)
        {
            return;
        }

        var kept = new List<KeyValuePair<string, object?>>(attributes.Count);
        foreach (var attribute in attributes)
        {
            if (RedactionOptions.IsDeniedName(attribute.Key))
            {
                continue;
            }

            kept.Add(new KeyValuePair<string, object?>(attribute.Key, Redact(attribute.Value)));
        }

        data.Attributes = kept;
    }

    private object? Redact(object? value) => value switch
    {
        string text => options.Redact(text),
        _ => value,
    };
}
