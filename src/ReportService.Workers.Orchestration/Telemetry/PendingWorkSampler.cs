using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.Workers.Orchestration.Jobs;
using Temporalio.Api.WorkflowService.V1;

namespace ReportService.Workers.Orchestration.Telemetry;

/// <summary>How often the work waiting on each priority class's queue is read.</summary>
public sealed class PendingWorkOptions : IValidatedSettings
{
    /// <summary>The section the setting is read from.</summary>
    public const string SectionPath = "Telemetry:PendingWork";

    /// <summary>The setting's name in the section.</summary>
    public const string SampleIntervalSetting = "SampleInterval";

    /// <summary>The default interval.</summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(15);

    /// <summary>The configured interval.</summary>
    public TimeSpan SampleInterval { get; set; } = DefaultSampleInterval;

    /// <inheritdoc />
    public IEnumerable<ConfigurationProblem> Validate()
    {
        if (SampleInterval < TimeSpan.FromSeconds(1) || SampleInterval > TimeSpan.FromMinutes(5))
        {
            yield return new ConfigurationProblem(SampleIntervalSetting, "must be between one second and five minutes");
        }
    }
}

/// <summary>
/// The engine's description of a task queue, read as the pending-work level needs it: the
/// approximate count of work waiting on it, from the queue's statistics. Pure, so it is tested
/// over a built description without an engine.
/// </summary>
public static class PendingWorkMapper
{
    /// <summary>The work waiting on the queue, or none when the description carries no statistics.</summary>
    /// <param name="description">The engine's description of the queue, statistics requested.</param>
    public static long? Pending(DescribeTaskQueueResponse description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.Stats is { } stats ? stats.ApproximateBacklogCount : null;
    }

    /// <summary>The six queues read: render and merge, each class from the highest to the lowest.</summary>
    public static IReadOnlyList<(string Queue, string Kind, string PriorityClass)> Queues { get; } =
    [
        .. PriorityTaskQueues.All(RenderPdfBlockStepOptions.Queue)
            .Select(queue => (queue, "render", JobWireValues.ToWire(PriorityTaskQueues.ClassOf(queue)))),
        .. PriorityTaskQueues.All(AssemblePdfStepOptions.Queue)
            .Select(queue => (queue, "merge", JobWireValues.ToWire(PriorityTaskQueues.ClassOf(queue)))),
    ];
}

/// <summary>
/// Reads, at a fixed interval, the work waiting on each priority class's render and merge queue
/// and keeps the latest value for the pending-work level. Every orchestration replica reads the
/// same engine value, so a reader aggregates the replicas with the maximum, never the sum. A
/// queue whose read fails reports no value until a read succeeds, and the failure is logged once
/// per interval.
/// </summary>
/// <param name="describe">Reads the engine's description of a task queue, statistics requested.</param>
/// <param name="metrics">The level the values are kept for.</param>
/// <param name="options">The interval.</param>
/// <param name="logger">Where a failed read is recorded.</param>
public sealed class PendingWorkSampler(
    Func<string, CancellationToken, Task<DescribeTaskQueueResponse>> describe,
    OrchestrationMetrics metrics,
    IOptions<PendingWorkOptions> options,
    ILogger<PendingWorkSampler> logger) : BackgroundService
{
    /// <summary>Reads every queue once and keeps the values.</summary>
    /// <param name="cancellationToken">Stops the pass.</param>
    /// <returns>How many queues could not be read.</returns>
    public async Task<int> SampleOnceAsync(CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var (queue, kind, priorityClass) in PendingWorkMapper.Queues)
        {
            long? pending;
            try
            {
                pending = PendingWorkMapper.Pending(await describe(queue, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                pending = null;
                failed++;
                if (failed == 1)
                {
                    logger.LogWarning("The work waiting on {TaskQueue} could not be read ({ExceptionType})", queue, failure.GetType().Name);
                }
            }

            metrics.ObservePendingWork(queue, kind, priorityClass, pending);
        }

        return failed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SampleInterval);
        do
        {
            await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
