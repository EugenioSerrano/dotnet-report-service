using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What looking up a job's output of a given format is asked to do.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="Format">The format the output produces.</param>
public sealed record FindOutputInput(string ReportJobId, OutputFormat Format);

/// <summary>The one output of the asked format a job has, or none when it asked for none.</summary>
/// <param name="OutputId">The identifier of the output, or <see langword="null"/> when the job has no output of that format.</param>
public sealed record FindOutputResult(string? OutputId);

/// <summary>
/// The per-format output lookup the job runs once for every registered output family before it
/// starts that family's execution - a workflow reads nothing itself.
/// </summary>
/// <param name="jobs">The job's outputs.</param>
/// <param name="loggers">The factory the job's log scope is opened on.</param>
public sealed class OutputLookupActivities(
    IJobStore jobs,
    ILoggerFactory loggers)
{
    /// <summary>The name the per-format output lookup is registered and scheduled under.</summary>
    public const string FindOutputName = "FindOutput";

    /// <summary>The code a state the step cannot use ends with.</summary>
    private const string ExecutionFailed = "EXECUTION_FAILED";

    /// <summary>
    /// Finds the one output of the asked format the job has. This lookup is nullable and no-throw
    /// when the format is absent (zero outputs of that format → no output), so a job may request
    /// any of the registered formats without another format's lookup failing it. It is fail-closed
    /// on ambiguity: more than one output of the requested format refuses, so a malformed job never
    /// silently produces one of several.
    /// </summary>
    /// <param name="input">The job and the format whose output is looked up.</param>
    /// <returns>The output's identifier, or <see langword="null"/> when the job has none of that format.</returns>
    [Activity(FindOutputName)]
    public async Task<FindOutputResult> FindOutputAsync(FindOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);

        var state = await jobs
            .FindForWorkflowAsync(input.ReportJobId, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false)
            ?? throw Refuse("the job is not recorded");

        var matching = state.Outputs.Where(output => output.Format == input.Format).ToList();
        if (matching.Count > 1)
        {
            throw Refuse($"the job asks for more than one {JobWireValues.ToStoredText(input.Format)} output");
        }

        return new FindOutputResult(matching.Count == 1 ? matching[0].OutputId : null);
    }

    private static ApplicationFailureException Refuse(string reason) =>
        new($"{ExecutionFailed}: {reason}", errorType: ExecutionFailed, nonRetryable: true);
}
