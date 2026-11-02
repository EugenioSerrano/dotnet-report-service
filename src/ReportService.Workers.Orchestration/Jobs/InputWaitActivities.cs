using Microsoft.Extensions.Logging;
using ReportService.Jobs;
using ReportService.ServiceDefaults.Telemetry;
using Temporalio.Activities;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What recording or clearing a job's waiting stage is asked to do: one job, by identifier.</summary>
/// <param name="ReportJobId">The identifier of the job.</param>
public sealed record InputWaitInput(string ReportJobId);

/// <summary>
/// The two steps that record and clear the stage a job carries while it waits for its
/// upload to be verified. Each is one conditional write, so each is safe to repeat: a
/// job that ended while the workflow was busy answers that it is no longer queued, and
/// a clear that arrives twice answers that there was nothing left to clear. Neither is
/// a failure, and neither touches the job's state, its code or its instants.
/// </summary>
public sealed class InputWaitActivities(IJobStore jobs, ILogger<InputWaitActivities> logger)
{
    /// <summary>The name the step that records the waiting stage is scheduled under.</summary>
    public const string BeginName = "BeginInputWait";

    /// <summary>The name the step that clears the waiting stage is scheduled under.</summary>
    public const string EndName = "EndInputWait";

    /// <summary>Records that the job waits for its upload, if it is still queued.</summary>
    /// <param name="input">The job to record.</param>
    [Activity(BeginName)]
    public async Task<BeginInputWaitOutcome> BeginAsync(InputWaitInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId);

        var outcome = await jobs
            .BeginInputWaitAsync(input.ReportJobId, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (outcome == BeginInputWaitOutcome.Applied)
        {
            logger.LogInformation("Job {ReportJobId} waiting for its upload", input.ReportJobId);
            return outcome;
        }

        logger.LogInformation("Job {ReportJobId} not queued; the wait was not recorded", input.ReportJobId);
        return outcome;
    }

    /// <summary>Clears the job's waiting stage, if it still carries it.</summary>
    /// <param name="input">The job to clear.</param>
    [Activity(EndName)]
    public async Task<EndInputWaitOutcome> EndAsync(InputWaitInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var context = JobContext.Begin(input.ReportJobId);

        var outcome = await jobs
            .EndInputWaitAsync(input.ReportJobId, ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);
        if (outcome == EndInputWaitOutcome.Applied)
        {
            logger.LogInformation("Job {ReportJobId} no longer waiting for its upload", input.ReportJobId);
            return outcome;
        }

        logger.LogInformation("Job {ReportJobId} was not waiting; the stage was left as it was", input.ReportJobId);
        return outcome;
    }
}
