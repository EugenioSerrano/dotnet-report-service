using ReportService.Jobs;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.WorkflowService.V1;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What one aging sample is asked to read.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputWorkflowId">The identifier of the output's execution, whose own work is described.</param>
public sealed record ReadPendingDispatchInput(string ReportJobId, string OutputWorkflowId);

/// <summary>What one aging sample saw.</summary>
/// <param name="Pending">Whether any of the output's render or assembly work is scheduled and not started.</param>
/// <param name="JobClass">The job's effective class as the store reads it, or none when the job is not found.</param>
public sealed record PendingDispatch(bool Pending, JobPriority? JobClass);

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
/// The aging sample of a PDF output, on the orchestration queue: whether the output's own render
/// or assembly work is waiting, and the job's current class. The engine is read through an
/// injected reader, so the step is tested without one.
/// </summary>
/// <param name="store">The job's effective class.</param>
/// <param name="describe">Reads the engine's description of an execution by its identifier.</param>
public sealed class PdfPendingDispatchActivities(
    IJobPriorityStore store,
    Func<string, CancellationToken, Task<DescribeWorkflowExecutionResponse>> describe)
{
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
}
