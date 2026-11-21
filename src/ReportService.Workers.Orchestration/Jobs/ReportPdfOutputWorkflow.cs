using Microsoft.Extensions.Logging;
using ReportService.Jobs;
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

    /// <summary>
    /// The codes a failure of the data or the template carries, and the only ones an output
    /// keeps without reading the cause record: the same data or the same template reported
    /// again reaches the same verdict, so nothing about the cause would change the answer.
    /// </summary>
    private static readonly IReadOnlyList<string> DeterministicRefusals =
    [
        "PAGINATION_FAILED",
        "DATA_INVALID",
        "TEMPLATE_INVALID",
        "UNSUPPORTED_CAPABILITY",
        "QUOTA_EXCEEDED",
    ];

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
            // the job is never left waiting on an output that will not end. The failure is
            // read as itself, never through CodeOf: a failure with no code of its own is
            // exactly the shape a worker that stopped produces, and the map below is what
            // tells it apart from a crash of the step.
            var carried = (failure.InnerException as ApplicationFailureException)?.ErrorType;
            var (code, reason) = await ExhaustionAsync(
                carried,
                () => Workflow.ExecuteActivityAsync(
                    (AttemptCauseActivities activities) => activities.FindLastAttemptCauseAsync(
                        new FindLastAttemptCauseInput(input.OutputId, 0)),
                    AttemptCauseActivities.Options())).ConfigureAwait(true);

            await FailTheOutputAsync(input, code, reason, metadata).ConfigureAwait(true);
        }
    }

    /// <summary>The code a carried failure keeps when the attempt that raised it hit the block's deadline.</summary>
    private const string RenderTimeout = "RENDER_TIMEOUT";

    /// <summary>
    /// The code and the reason an output that has no attempt left ends with, from the code its
    /// last failure carries and the cause that failure's last attempt recorded. A refusal of
    /// the data or the template keeps its own code and reads nothing; every other failure reads
    /// the last recorded cause, so a deadline is reported as the timeout it was and a crash,
    /// which records nothing, as the generic code. The one read this map makes is the step's
    /// only call here, and the map never publishes anything.
    /// </summary>
    /// <param name="carried">The code the last failure carries, or <see langword="null"/> when it carries none.</param>
    /// <param name="read">The read of the block's last recorded cause.</param>
    /// <returns>The code and the reason the output ends with.</returns>
    public static async Task<(string Code, string? Reason)> ExhaustionAsync(
        string? carried,
        Func<Task<LastAttemptCause?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        // A refusal of the data or the template is deterministic: the same input reaches the
        // same verdict, so it keeps its code, records no cause and reads none.
        if (IsDeterministicRefusal(carried))
        {
            return (carried!, null);
        }

        var last = await read().ConfigureAwait(true);

        // The carried code is read before the cause: an attempt that raised the timeout keeps
        // it, and a failure with no code of its own is the timeout only when an attempt of the
        // block recorded a deadline. Anything else is the generic code - a crash records
        // nothing, so the absence of a row is the crash arm's whole signal.
        var code = string.Equals(carried, RenderTimeout, StringComparison.Ordinal)
            || AttemptCauseVocabulary.IsDeadline(last?.Cause)
                ? RenderTimeout
                : ExecutionFailed;
        return (code, AttemptCauseVocabulary.ReasonOf(last?.Cause));
    }

    /// <summary>Whether a carried code is one of the deterministic refusals.</summary>
    /// <param name="carried">The carried error type, when the failure has one.</param>
    /// <returns>Whether the code keeps itself without reading a cause.</returns>
    private static bool IsDeterministicRefusal(string? carried) =>
        carried is { Length: > 0 } code && DeterministicRefusals.Contains(code, StringComparer.Ordinal);

    /// <summary>The code a failure's carried error type keeps, or the generic code.</summary>
    /// <param name="carried">The carried error type, when the failure has one.</param>
    /// <returns>The code to end the output with.</returns>
    private static string CodeOf(string? carried) =>
        carried is { Length: > 0 } code && DomainCodes.Contains(code) ? code : ExecutionFailed;

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
        "DATA_INVALID",
        "TEMPLATE_INVALID",
        "EXECUTION_FAILED",
    };

    private static async Task FailTheOutputAsync(
        ReportPdfOutputInput input,
        string code,
        string? reason,
        ActivityOptions options)
    {
        Workflow.Logger.LogWarning(
            "Output {OutputId} ended with {ErrorCode}",
            input.OutputId,
            code);

        await Workflow.ExecuteActivityAsync(
            (JobTransitionActivities activities) => activities.FailOutputAsync(
                new FailOutputInput(input.OutputId, code, reason)),
            options).ConfigureAwait(true);
    }
}
