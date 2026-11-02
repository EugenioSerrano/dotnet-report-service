using Microsoft.Extensions.Logging;
using ReportService.Uploads;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the job workflow is asked to run: one report job, by identifier.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record ReportJobInput(string ReportJobId);

/// <summary>
/// One report job, as a workflow: it checks the job's data reference and, when that
/// reference cannot be used, ends the job with the condition that made it unusable.
/// It holds no clock, no random value and no I/O of its own, so its history replays
/// exactly — everything that can fail or vary happens inside its activities. It
/// starts no timer and has no loop: an input that is not usable yet stops the run at
/// its continuation point and leaves the job queued, and the step that takes a ready
/// input to its end is added here by the work that prints.
/// </summary>
[Workflow(WorkflowType)]
public sealed class ReportJobWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "ReportJob";

    /// <summary>The queue the workflow itself runs on.</summary>
    public const string OrchestrationQueue = "report-orchestration";

    /// <summary>The prefix of the workflow's identifier: one execution per job.</summary>
    public const string WorkflowIdPrefix = "report-job";

    /// <summary>The identifier of the one execution that runs <paramref name="reportJobId"/>.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public static string WorkflowIdFor(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return $"{WorkflowIdPrefix}/{reportJobId}";
    }

    /// <summary>Runs one job up to its continuation point.</summary>
    /// <param name="input">The job to run.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportJobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var options = ReportJobActivityOptions.Create();

        var resolution = await Workflow.ExecuteActivityAsync(
            (ReportJobActivities activities) => activities.ResolveInputAsync(new ResolveInputInput(input.ReportJobId)),
            options).ConfigureAwait(true);

        switch (resolution.Outcome)
        {
            case InputOutcome.Ready:
                // The point where the steps that take a ready input to its end are added;
                // until they exist the run completes and the job stays as the acceptance left it.
                Workflow.Logger.LogInformation("Job {ReportJobId} input ready", input.ReportJobId);
                return;

            case InputOutcome.Verifying:
                // The point where the bounded wait for a confirming input is added; the job
                // stays queued until then, because this run neither waits nor polls.
                return;

            case InputOutcome.Rejected:
                await FailAsync(input.ReportJobId, UploadWireValues.ToStoredText(resolution.RejectedWith()), "input-rejected", options).ConfigureAwait(true);
                return;

            case InputOutcome.Expired:
                await FailAsync(input.ReportJobId, ReportJobActivities.InputNotFound, "input-expired", options).ConfigureAwait(true);
                return;

            case InputOutcome.Terminal:
                return;

            default:
                throw new InvalidOperationException("the input check answered an outcome this workflow does not know.");
        }
    }

    private static Task FailAsync(string reportJobId, string errorCode, string reason, Temporalio.Workflows.ActivityOptions options) =>
        Workflow.ExecuteActivityAsync(
            (ReportJobActivities activities) => activities.FailJobAsync(new FailJobInput(reportJobId, errorCode, reason)),
            options);
}
