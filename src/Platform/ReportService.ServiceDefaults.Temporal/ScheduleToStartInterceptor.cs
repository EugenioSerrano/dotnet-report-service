using System.Diagnostics.Metrics;
using Temporalio.Activities;
using Temporalio.Worker.Interceptors;

namespace ReportService.ServiceDefaults.Temporal;

/// <summary>
/// The schedule-to-start latency of every activity attempt a worker takes from its queue: how
/// long the attempt waited between being scheduled and being started, recorded once per attempt
/// on the host's own meter. Its attributes are the queue, the kind of work and the priority class
/// the queue serves - closed, low-cardinality values, never a job.
/// </summary>
public sealed class ScheduleToStartRecorder : IDisposable
{
    /// <summary>The histogram's name.</summary>
    public const string InstrumentName = "reportservice.taskqueue.schedule_to_start.duration";

    private readonly Meter _meter;
    private readonly Histogram<double> _duration;
    private readonly string _kind;
    private readonly Func<string, string> _classOf;

    /// <summary>Creates the histogram on the host's meter.</summary>
    /// <param name="identity">The host's meter identity.</param>
    /// <param name="kind">The kind of work the worker takes: render or merge.</param>
    /// <param name="classOf">The priority class a queue serves, as its wire value.</param>
    public ScheduleToStartRecorder(MeterIdentity identity, string kind, Func<string, string> classOf)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(classOf);
        _meter = new Meter(identity.Name);
        _kind = kind;
        _classOf = classOf;
        _duration = _meter.CreateHistogram<double>(
            InstrumentName,
            unit: "s",
            description: "How long an activity attempt waited between being scheduled and being started, per task queue.");
    }

    /// <summary>Records one attempt's wait; a start reported before its schedule records zero.</summary>
    /// <param name="queue">The task queue the attempt was taken from.</param>
    /// <param name="scheduled">When the attempt was scheduled.</param>
    /// <param name="started">When it started.</param>
    public void Record(string queue, DateTime scheduled, DateTime started)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        var seconds = Math.Max(0, (started - scheduled).TotalSeconds);
        _duration.Record(
            seconds,
            new KeyValuePair<string, object?>("task_queue", queue),
            new KeyValuePair<string, object?>("kind", _kind),
            new KeyValuePair<string, object?>("priority_class", _classOf(queue)));
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}

/// <summary>Records each activity attempt's schedule-to-start latency before the activity runs.</summary>
/// <param name="recorder">Where the latency is recorded.</param>
public sealed class ScheduleToStartInterceptor(ScheduleToStartRecorder recorder) : IWorkerInterceptor
{
    /// <inheritdoc />
    public ActivityInboundInterceptor InterceptActivity(ActivityInboundInterceptor nextInterceptor) =>
        new Inbound(nextInterceptor, recorder);

    private sealed class Inbound(ActivityInboundInterceptor next, ScheduleToStartRecorder recorder)
        : ActivityInboundInterceptor(next)
    {
        public override Task<object?> ExecuteActivityAsync(ExecuteActivityInput input)
        {
            var info = ActivityExecutionContext.Current.Info;
            recorder.Record(info.TaskQueue, info.CurrentAttemptScheduledTime, info.StartedTime);
            return base.ExecuteActivityAsync(input);
        }
    }
}
