using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportService.Jobs;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What a job's dispatch is read for.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ReadJobDispatchInput(string ReportJobId);

/// <summary>The class a job dispatches on and the deployment's aging wait, recorded once per execution.</summary>
/// <param name="PriorityClass">The job's effective class; normal when the job is not found.</param>
/// <param name="AgingWaitSeconds">The deployment's aging wait, in seconds.</param>
public sealed record JobDispatch(JobPriority PriorityClass, double AgingWaitSeconds);

/// <summary>What one aging sample is asked to read.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputWorkflowId">The identifier of the output's execution, whose own work is described.</param>
public sealed record ReadPendingDispatchInput(string ReportJobId, string OutputWorkflowId);

/// <summary>What one aging sample saw.</summary>
/// <param name="Pending">Whether any of the output's render or assembly work is scheduled and not started.</param>
/// <param name="JobClass">The job's effective class as the store reads it, or none when the job is not found.</param>
public sealed record PendingDispatch(bool Pending, JobPriority? JobClass);

/// <summary>What a promotion is asked to do.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="PendingSeconds">The output's pending time when it reached the wait.</param>
public sealed record PromoteToNormalInput(string ReportJobId, double PendingSeconds);

/// <summary>
/// The engine's description of an output's execution, read as the aging rule needs it: whether
/// any of its render or assembly activities is scheduled and not started. Pure, so it is tested
/// over a built description without an engine.
/// </summary>
public static class PendingDispatchMapper
{
    /// <summary>The activity types whose waiting counts as pending render or merge work.</summary>
    public static readonly IReadOnlyList<string> WorkTypes =
        [PdfOutputStepContracts.RenderPdfBlock, AssemblePdfStepContracts.AssemblePdf];

    /// <summary>Whether the description shows render or assembly work scheduled and not started.</summary>
    /// <param name="description">The engine's description of the execution.</param>
    public static bool AnyWaiting(DescribeWorkflowExecutionResponse description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.PendingActivities.Any(activity =>
            activity.State == PendingActivityState.Scheduled
            && WorkTypes.Contains(activity.ActivityType?.Name, StringComparer.Ordinal));
    }
}

/// <summary>
/// The steps of the job's dispatch by class and its aging, all on the orchestration queue:
/// reading the class and the wait once, sampling an output's pending work, and promoting the
/// job. The engine is read through an injected reader, so the steps are tested without one.
/// </summary>
/// <param name="store">The job's effective class.</param>
/// <param name="aging">The deployment's aging wait.</param>
/// <param name="describe">Reads the engine's description of an execution by its identifier.</param>
/// <param name="metrics">The promotion counter.</param>
/// <param name="logger">Where a promotion is recorded.</param>
public sealed class JobDispatchActivities(
    IJobPriorityStore store,
    IOptions<PriorityAgingOptions> aging,
    Func<string, CancellationToken, Task<DescribeWorkflowExecutionResponse>> describe,
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

    /// <summary>Samples whether the output's work is waiting, and the job's current class.</summary>
    /// <param name="input">The job and the output's execution.</param>
    [Activity]
    public async Task<PendingDispatch> ReadPendingDispatchAsync(ReadPendingDispatchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current.CancellationToken : CancellationToken.None;
        var description = await describe(input.OutputWorkflowId, token).ConfigureAwait(false);
        var jobClass = await store.ReadEffectiveClassAsync(input.ReportJobId, token).ConfigureAwait(false);
        return new PendingDispatch(PendingDispatchMapper.AnyWaiting(description), jobClass);
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
