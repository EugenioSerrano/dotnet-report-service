using System.Diagnostics;
using OpenTelemetry;

namespace ReportService.ServiceDefaults.Telemetry;

/// <summary>
/// Tags every span of a unit of work with <c>job.id</c>. It runs on span start,
/// so child spans — an outgoing HTTP call's span included — carry the
/// identifier without a caller having to pass it; a span that already carries
/// one keeps it. The processor sees activities only: it cannot touch a metric.
/// </summary>
internal sealed class JobIdActivityProcessor : BaseProcessor<Activity>
{
    /// <inheritdoc />
    public override void OnStart(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (JobContext.Current is not { } jobId)
        {
            return;
        }

        if (data.GetTagItem(JobContext.ActivityTagName) is null)
        {
            data.SetTag(JobContext.ActivityTagName, jobId);
        }
    }
}
