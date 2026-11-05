using Microsoft.Extensions.Logging;
using ReportService.ServiceDefaults.Telemetry;
using ReportService.Workers.Orchestration.Telemetry;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What one output's execution is asked to produce: the PDF output of one job.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the job's PDF output.</param>
public sealed record ReportPdfOutputInput(string ReportJobId, string OutputId);

/// <summary>
/// The one PDF output of a job, as its own execution: it starts the output, has its single
/// block printed and verified on the PDF worker, and publishes the verified block as the
/// output's one artifact in the transaction that also marks the output succeeded. A failure
/// that ends the work ends the output with the code the failure carries - a block that failed
/// its checks, a package this increment cannot print, a value beyond the render's guards, or,
/// for anything else, the code that says the step itself could not finish - and the output
/// is never left running: the job that started it closes from the output's own state.
/// It holds no clock, no random value and no I/O of its own, so its history replays exactly.
/// </summary>
[Workflow(WorkflowType)]
public sealed class ReportPdfOutputWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "ReportPdfOutput";

    /// <summary>The suffix of the child execution's identifier: one execution per output.</summary>
    public const string WorkflowIdSuffix = "/output/pdf";

    /// <summary>The code an output ends with when the failure carries no code of its own.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>The identifier of the one execution that produces <paramref name="reportJobId"/>'s PDF output.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public static string WorkflowIdFor(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return ReportJobWorkflow.WorkflowIdFor(reportJobId) + WorkflowIdSuffix;
    }

    /// <summary>Produces one output of one job.</summary>
    /// <param name="input">The job and the output.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportPdfOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var metadata = ReportJobActivityOptions.Create();

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.TransitionOutputAsync(
                new TransitionOutputInput(input.OutputId)),
            metadata).ConfigureAwait(true);

        try
        {
            var part = await Workflow.ExecuteActivityAsync(
                (IRenderPdfBlockStep step) => step.RunAsync(new RenderPdfBlockStepInput(
                    input.ReportJobId,
                    input.OutputId,
                    0)),
                RenderPdfBlockStepOptions.Create()).ConfigureAwait(true);

            await Workflow.ExecuteActivityAsync(
                (PdfOutputActivities activities) => activities.PublishOutputAsync(new PublishOutputInput(
                    input.ReportJobId,
                    input.OutputId,
                    part.PartKey,
                    part.Sha256,
                    part.SizeBytes,
                    part.PageCount)),
                metadata).ConfigureAwait(true);
        }
        catch (ActivityFailureException failure)
        {
            // The render or the publication could not finish: the output ends with the code
            // the failure carries, and one that carries none ends with the generic code, so
            // the job is never left waiting on an output that will not end.
            await FailTheOutputAsync(input, CodeOf(failure.InnerException), metadata).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The code an output ends with for a failure: the failure's own code when it is one of
    /// the codes this path raises, and the generic code otherwise. A runtime exception reaches
    /// the workflow with its own type name where a code would be, and that type is not a
    /// consumer's answer.
    /// </summary>
    /// <param name="failure">The failure, as the engine reported it inside the step's failure.</param>
    /// <returns>The code to end the output with.</returns>
    public static string CodeOf(Exception? failure) =>
        failure is ApplicationFailureException application
        && application.ErrorType is { Length: > 0 } code
        && DomainCodes.Contains(code)
            ? code
            : ExecutionFailed;

    /// <summary>The codes the steps of a printed output raise, and the only ones an output keeps.</summary>
    private static readonly HashSet<string> DomainCodes = new(StringComparer.Ordinal)
    {
        "PAGINATION_FAILED",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
        "RENDER_TIMEOUT",
        "EXECUTION_FAILED",
    };

    private static async Task FailTheOutputAsync(ReportPdfOutputInput input, string code, ActivityOptions options)
    {
        Workflow.Logger.LogWarning(
            "Output {OutputId} ended with {ErrorCode}",
            input.OutputId,
            code);

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.FailOutputAsync(
                new FailOutputInput(input.OutputId, code)),
            options).ConfigureAwait(true);
    }
}
