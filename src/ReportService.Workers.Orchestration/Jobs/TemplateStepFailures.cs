using ReportService.Jobs;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The decision a job's template step makes about a failure: whether the failure carries
/// one of the step's codes, and what the job records when it does. It is one pure function
/// so the answer an engine reports and the answer a job stores can be read and asserted
/// without an engine: the code is the failure's type, the reason is the constant the code
/// stands for, and the message is what the failing step said, cut at the length a job may
/// hold. A failure with no code of its own - a step that ran out of attempts, or a timeout -
/// maps to the step's own failure.
/// </summary>
public static class TemplateStepFailures
{
    /// <summary>What a job records when its template step fails.</summary>
    /// <param name="Code">The code the job and its output end with.</param>
    /// <param name="Reason">The constant recorded beside the code.</param>
    /// <param name="Message">The text the job carries.</param>
    public sealed record Mapped(string Code, string Reason, string Message)
    {
        /// <summary>True when the message had to be cut to the length a job may hold.</summary>
        public bool WasCut(string original) => original.Length != Message.Length;
    }

    /// <summary>
    /// Maps the failure an activity call ended with, when it carries one of the step's codes.
    /// </summary>
    /// <param name="failure">The failure inside the activity failure, as the engine reports it.</param>
    /// <param name="mapped">What the job records, when the failure carries a code.</param>
    /// <returns>True when the failure is one the step answers with; otherwise false.</returns>
    public static bool TryMap(Exception? failure, out Mapped mapped)
    {
        var application = failure as ApplicationFailureException;
        if (application is null || !TemplateFailureCodes.IsTemplateCode(application.ErrorType))
        {
            mapped = StepFailed();
            return false;
        }

        var text = application.Message;
        mapped = new Mapped(
            application.ErrorType!,
            TemplateFailureCodes.ReasonOf(application.ErrorType!, text),
            TemplateFailureCodes.Cap(text));
        return true;
    }

    /// <summary>
    /// True when the failure is the one an unreachable template source raises, which a
    /// by-name reference answers with the version recorded at acceptance.
    /// </summary>
    /// <param name="failure">The failure inside the activity failure, as the engine reports it.</param>
    public static bool IsSourceUnavailable(Exception? failure) =>
        failure is ApplicationFailureException application
        && string.Equals(application.ErrorType, TemplateFailureCodes.SourceUnavailable, StringComparison.Ordinal);
    /// <summary>What a job records when its step failed for a cause that carries no code of its own.</summary>
    public static Mapped StepFailed() => new(
        TemplateFailureCodes.ExecutionFailed,
        TemplateFailureCodes.StepFailedReason,
        TemplateFailureCodes.StepFailedMessage);
}
