using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What one output's execution is asked to produce: the HTML output of one job.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="OutputId">The identifier of the job's HTML output.</param>
public sealed record ReportHtmlOutputInput(string ReportJobId, string OutputId);

/// <summary>
/// The one HTML output of a job, as its own execution: it starts the output, has it assembled
/// on the <c>report-html</c> queue (no Chromium), and publishes the assembled <c>.html</c> as the
/// output's one artifact in the transaction that also marks the output succeeded. A failure that
/// ends the work ends the output with the code the failure carries — a package this increment
/// cannot produce HTML from, an invalid template or data, a value beyond the guards, or, for
/// anything else, the code that says the step itself could not finish — and the output is never
/// left running. Unlike the PDF output it has no attempt-cause or exhaustion read: the HTML
/// assembly has no render-deadline or pagination path, so the carried code (or the generic)
/// decides directly. It holds no clock, no random value and no I/O of its own, so its history
/// replays exactly.
/// </summary>
[Workflow(WorkflowType)]
public sealed class ReportHtmlOutputWorkflow
{
    /// <summary>The name the workflow is registered and scheduled under.</summary>
    public const string WorkflowType = "ReportHtmlOutput";

    /// <summary>The suffix of the child execution's identifier: one execution per output.</summary>
    public const string WorkflowIdSuffix = "/output/html";

    /// <summary>The code an output ends with when the failure carries no code of its own.</summary>
    public const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>
    /// The codes the HTML assembly raises, and the only ones an output keeps. The PDF-only
    /// render-deadline and pagination codes are not in this set: a failure carrying one of them
    /// ends the HTML output with the generic code.
    /// </summary>
    private static readonly HashSet<string> DomainCodes = new(StringComparer.Ordinal)
    {
        "UNSUPPORTED_CAPABILITY",
        "TEMPLATE_INVALID",
        "DATA_INVALID",
        "QUOTA_EXCEEDED",
        "EXECUTION_FAILED",
    };

    /// <summary>The identifier of the one execution that produces <paramref name="reportJobId"/>'s HTML output.</summary>
    /// <param name="reportJobId">The identifier of the job.</param>
    public static string WorkflowIdFor(string reportJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJobId);
        return ReportJobWorkflow.WorkflowIdFor(reportJobId) + WorkflowIdSuffix;
    }

    /// <summary>
    /// The code an output ends with for a failure: the failure's own code when it is one of the
    /// codes the HTML assembly raises, and the generic code otherwise. A runtime exception reaches
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

    /// <summary>Produces one output of one job.</summary>
    /// <param name="input">The job and the output.</param>
    [WorkflowRun]
    public async Task RunAsync(ReportHtmlOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var metadata = ReportJobActivityOptions.Create();

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.TransitionOutputAsync(
                new TransitionOutputInput(input.OutputId)),
            metadata).ConfigureAwait(true);

        try
        {
            var assembled = await Workflow.ExecuteActivityAsync(
                (IAssembleHtmlStep step) => step.RunAsync(new AssembleHtmlStepInput(
                    input.ReportJobId,
                    input.OutputId)),
                AssembleHtmlStepOptions.Create()).ConfigureAwait(true);

            await Workflow.ExecuteActivityAsync(
                (HtmlOutputActivities activities) => activities.PublishHtmlOutputAsync(new PublishHtmlOutputInput(
                    input.ReportJobId,
                    input.OutputId,
                    assembled.ObjectKey,
                    assembled.Sha256,
                    assembled.SizeBytes)),
                metadata).ConfigureAwait(true);
        }
        catch (ActivityFailureException failure)
            when (!TemporalException.IsCanceledException(failure))
        {
            // The assembly or the publication could not finish: the output ends with the code the
            // failure carries, and one that carries none (or a non-HTML code) ends with the
            // generic code, so the job is never left waiting on an output that will not end. A
            // cancellation is not a failure: the job's finalisation owns the output then.
            var code = CodeOf(failure.InnerException);
            Workflow.Logger.LogWarning("Output {OutputId} ended with {ErrorCode}", input.OutputId, code);
            await Workflow.ExecuteActivityAsync(
                (JobTransitionActivities activities) => activities.FailOutputAsync(
                    new FailOutputInput(input.OutputId, code, null)),
                metadata).ConfigureAwait(true);
        }
    }
}
