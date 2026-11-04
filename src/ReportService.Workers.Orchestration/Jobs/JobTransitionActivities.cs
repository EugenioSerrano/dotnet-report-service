using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What moving a job to another state is asked to do.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
/// <param name="To">The state to move it to.</param>
public sealed record TransitionJobInput(string ReportJobId, ReportJobStatus To);

/// <summary>What starting an output is asked to do.</summary>
/// <param name="OutputId">The identifier of the output.</param>
public sealed record TransitionOutputInput(string OutputId);

/// <summary>What ending one output is asked to do.</summary>
/// <param name="OutputId">The identifier of the output.</param>
/// <param name="ErrorCode">The code of the condition that ended it.</param>
public sealed record FailOutputInput(string OutputId, string ErrorCode);

/// <summary>
/// The short metadata steps a job's states are written with, each one a conditional
/// statement on the orchestration queue: they move a job forward, start an output and
/// end one output. Their instants come from the process's clock, and a step that
/// arrives twice answers what the record already says instead of writing again.
/// </summary>
public sealed class JobTransitionActivities(IJobStore jobs, TimeProvider clock, ILoggerFactory loggers, ILogger<JobTransitionActivities> logger)
{
    /// <summary>The name the job transition is registered and scheduled under.</summary>
    public const string TransitionJobName = "TransitionJob";

    /// <summary>The name the output start is registered and scheduled under.</summary>
    public const string TransitionOutputName = "TransitionOutput";

    /// <summary>The name the output failure is registered and scheduled under.</summary>
    public const string FailOutputName = "FailOutput";

    /// <summary>The states a job is moved forward from, for each state it can be moved to.</summary>
    /// <param name="to">The state to move the job to.</param>
    /// <remarks>
    /// Only the states the job's path passes through are here: a job is queued, moves to
    /// validating, then to running. It ends through the step that closes it, which writes the
    /// terminal state from the outcome of its outputs, so no transition of this step targets one.
    /// </remarks>
    public static IReadOnlyList<ReportJobStatus> SourcesOf(ReportJobStatus to) => to switch
    {
        ReportJobStatus.Validating => [ReportJobStatus.Queued],
        ReportJobStatus.Running => [ReportJobStatus.Validating],
        _ => [],
    };

    /// <summary>Moves one job forward to the state the workflow asked for.</summary>
    /// <param name="input">The job and the state to move it to.</param>
    [Activity(TransitionJobName)]
    public async Task<TransitionOutcome> TransitionJobAsync(TransitionJobInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId, loggers);

        var outcome = await jobs.TransitionJobAsync(
            input.ReportJobId,
            SourcesOf(input.To),
            input.To,
            clock.GetUtcNow(),
            ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Job {ReportJobId} transition to {Status}: {Outcome}",
            input.ReportJobId,
            ReportJobMetricsName(input.To),
            outcome);
        return outcome;
    }

    /// <summary>Starts one output of a job.</summary>
    /// <param name="input">The output to start.</param>
    [Activity(TransitionOutputName)]
    public Task<TransitionOutcome> TransitionOutputAsync(TransitionOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return jobs.TransitionOutputAsync(
            input.OutputId,
            clock.GetUtcNow(),
            ActivityExecutionContext.Current.CancellationToken);
    }

    /// <summary>Ends one output that has not ended yet with the code of the condition.</summary>
    /// <param name="input">The output and the condition that ended it.</param>
    [Activity(FailOutputName)]
    public async Task<OutputFailureOutcome> FailOutputAsync(FailOutputInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var outcome = await jobs.FailOutputAsync(
            input.OutputId,
            input.ErrorCode,
            clock.GetUtcNow(),
            ActivityExecutionContext.Current.CancellationToken).ConfigureAwait(false);

        if (outcome == ReportService.Jobs.OutputFailureOutcome.Applied)
        {
            logger.LogInformation("Output {OutputId} ended with {ErrorCode}", input.OutputId, input.ErrorCode);
        }

        return outcome;
    }

    private static string ReportJobMetricsName(ReportJobStatus status) => JobWireValues.ToStoredText(status);
}
