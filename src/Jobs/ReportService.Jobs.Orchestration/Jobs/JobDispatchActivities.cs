using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What a job's dispatch is read for.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ReadJobDispatchInput(string ReportJobId);

/// <summary>The class a job dispatches on and the deployment's aging wait, recorded once per execution.</summary>
/// <param name="PriorityClass">The job's effective class; normal when the job is not found.</param>
/// <param name="AgingWaitSeconds">The deployment's aging wait, in seconds.</param>
public sealed record JobDispatch(JobPriority PriorityClass, double AgingWaitSeconds);

/// <summary>What a promotion is asked to do.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="PendingSeconds">The output's pending time when it reached the wait.</param>
public sealed record PromoteToNormalInput(string ReportJobId, double PendingSeconds);

/// <summary>
/// The steps of the job's dispatch by class and its aging that belong to the job, all on the
/// orchestration queue: reading the class and the wait once, and promoting the job. Sampling an
/// output's own pending work belongs to the output family whose work it reads.
/// </summary>
/// <param name="store">The job's effective class.</param>
/// <param name="aging">The deployment's aging wait.</param>
/// <param name="metrics">The promotion counter.</param>
/// <param name="logger">Where a promotion is recorded.</param>
public sealed class JobDispatchActivities(
    IJobPriorityStore store,
    IOptions<PriorityAgingOptions> aging,
    OrchestrationMetrics metrics,
    ILogger<JobDispatchActivities> logger)
{
    /// <summary>Reads the job's effective class and the deployment's aging wait.</summary>
    /// <param name="input">The job.</param>
    [Activity]
    public async Task<JobDispatch> ReadJobDispatchAsync(ReadJobDispatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current.CancellationToken : CancellationToken.None;
        var stored = await store.ReadEffectiveClassAsync(input.ReportJobId, token).ConfigureAwait(false);
        return new JobDispatch(stored ?? JobPriority.Normal, aging.Value.Wait.TotalSeconds);
    }

    /// <summary>
    /// Promotes the job from low to normal, and answers whether this call moved it. Only a call
    /// that moved it logs and counts, so a retried step counts once.
    /// </summary>
    /// <param name="input">The job and the pending time that reached the wait.</param>
    [Activity]
    public async Task<bool> PromoteToNormalAsync(PromoteToNormalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current.CancellationToken : CancellationToken.None;
        var moved = await store.PromoteEffectiveClassAsync(input.ReportJobId, token).ConfigureAwait(false);
        if (moved)
        {
            logger.LogInformation(
                "Job {ReportJobId} promoted from low to normal for its later dispatch after {PendingSeconds} s pending; its declared priority is unchanged",
                input.ReportJobId,
                Math.Round(input.PendingSeconds));
            metrics.PriorityPromotion("low", "normal");
        }

        return moved;
    }
}
