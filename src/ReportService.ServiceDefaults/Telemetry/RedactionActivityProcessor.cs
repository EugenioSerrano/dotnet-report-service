using System.Diagnostics;
using OpenTelemetry;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// Removes sensitive content from a span before it is exported: every
/// configured sensitive value and the storage root path is replaced with
/// <see cref="RedactionOptions.Replacement"/> and an over-long tag value is
/// truncated.
/// <para>
/// A span's own tag cannot be removed once it is set — the activity offers no
/// removal — so a tag whose name matches the deny-list keeps its name and its
/// value becomes <see cref="RedactionOptions.Replacement"/>. What the control
/// protects is the value, and the value is gone.
/// </para>
/// <para>
/// A span's <b>events</b> cannot be redacted at all: the framework keeps an
/// event's tags in a list it exposes only for enumeration, with no way to
/// remove or replace one from outside, so this processor leaves them as the
/// caller wrote them. The rule for an event is therefore the caller's, exactly
/// as it is for a metric tag — see docs/observability.md — and the tests record
/// the limitation instead of pretending otherwise.
/// </para>
/// </summary>
internal sealed class RedactionActivityProcessor(RedactionOptions options) : BaseProcessor<Activity>
{
    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        RedactTags(data);
    }

    private void RedactTags(Activity activity)
    {
        List<KeyValuePair<string, object?>> changes = [];
        foreach (var tag in activity.TagObjects)
        {
            if (RedactionOptions.IsDeniedName(tag.Key))
            {
                changes.Add(new KeyValuePair<string, object?>(tag.Key, RedactionOptions.Replacement));
                continue;
            }

            if (tag.Value is string text
                && options.Redact(text) is { } redacted
                && !string.Equals(redacted, text, StringComparison.Ordinal))
            {
                changes.Add(new KeyValuePair<string, object?>(tag.Key, redacted));
            }
        }

        foreach (var change in changes)
        {
            activity.SetTag(change.Key, change.Value);
        }
    }
}
