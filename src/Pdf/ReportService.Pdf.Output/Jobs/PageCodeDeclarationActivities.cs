using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using ReportService.TemplatePackages.Records;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>
/// The page-code declaration read of a PDF output, on the orchestration queue: whether the job's
/// taken template version declares a per-page code. It is registered under the name the job core
/// keeps for it, so the scheduled activity is the same one it always was.
/// </summary>
/// <param name="jobs">The job's template reference.</param>
/// <param name="versions">The taken template versions.</param>
/// <param name="loggers">The factory the job's log scope is opened on.</param>
public sealed class PageCodeDeclarationActivities(
    IJobStore jobs,
    ITemplateVersionStore versions,
    ILoggerFactory loggers)
{
    /// <summary>
    /// Reads whether the job's taken template version declares a per-page code - the
    /// fact that sends a one-block output through the assembly. It reads the version's
    /// manifest only, never the job's data.
    /// </summary>
    /// <param name="input">The job.</param>
    /// <returns>Whether the version declares a per-page code.</returns>
    [Activity(TemplateResolutionActivities.ReadPageCodeDeclarationName)]
    public async Task<PageCodeDeclarationResult> ReadPageCodeDeclarationAsync(PageCodeDeclarationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var reference = await jobs.FindTemplateReferenceAsync(input.ReportJobId, cancellationToken).ConfigureAwait(false);
        if (reference?.Pin is not { } pin)
        {
            throw new ApplicationFailureException(
                "the job's template version was never resolved",
                errorType: "EXECUTION_FAILED",
                nonRetryable: true);
        }

        var record = await versions.FindAsync(pin.TemplateId, pin.ResolvedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new ApplicationFailureException(
                "the job's template version was never taken",
                errorType: "EXECUTION_FAILED",
                nonRetryable: true);
        return new PageCodeDeclarationResult(record.Manifest.Outputs?.Pdf?.PageCode is not null);
    }
}
