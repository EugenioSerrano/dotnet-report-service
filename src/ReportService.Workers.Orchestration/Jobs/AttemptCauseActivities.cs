using ReportService.Jobs;
using Temporalio.Activities;
using Temporalio.Workflows;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What reading the last cause of one block is asked to do.</summary>
/// <param name="OutputId">The identifier of the output the block belongs to.</param>
/// <param name="BlockIndex">The block's position in the output's partition, from 0.</param>
public sealed record FindLastAttemptCauseInput(string OutputId, int BlockIndex);

/// <summary>
/// The last cause a rendering attempt recorded for one block: the attempt number the engine
/// reported and the deadline class the attempt ended with. It carries no instant and nothing
/// else the record holds - a workflow never reads a clock-derived value of its own.
/// </summary>
/// <param name="Attempt">The attempt number the cause was recorded for, from 1.</param>
/// <param name="Cause">The deadline class the attempt ended with.</param>
public sealed record LastAttemptCause(int Attempt, string Cause);

/// <summary>
/// The read of the cause a block's last recorded attempt ended with, as a step of the
/// orchestration queue: a workflow reads nothing itself, so the answer arrives through this
/// one metadata step. The read is an identifier lookup - the row with the greatest attempt
/// for the output and the block - and answers an empty result when no attempt recorded one.
/// It holds no clock, no clock-derived value and no state of its own, so a repeat of the
/// step answers the same row.
/// </summary>
public sealed class AttemptCauseActivities(IJobStore jobs)
{
    /// <summary>The name the read is registered and scheduled under.</summary>
    public const string FindLastAttemptCauseName = "FindLastAttemptCause";

    /// <summary>
    /// The execution settings of the read: the metadata class - the orchestration queue, a
    /// 30 s bound on one attempt, the waits between attempts, and no attempt limit, so a
    /// database outage makes the output wait rather than fail.
    /// </summary>
    /// <returns>The settings of one read.</returns>
    public static ActivityOptions Options() => ReportJobActivityOptions.Create();

    /// <summary>Reads the last cause recorded for one block of an output.</summary>
    /// <param name="input">The output and the block.</param>
    /// <returns>The last recorded cause, or <see langword="null"/> when none was recorded.</returns>
    [Activity(FindLastAttemptCauseName)]
    public async Task<LastAttemptCause?> FindLastAttemptCauseAsync(FindLastAttemptCauseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        System.Diagnostics.Activity.Current?.SetTag("output.id", input.OutputId);
        System.Diagnostics.Activity.Current?.SetTag("block.index", input.BlockIndex);

        var row = await jobs
            .FindLastAttemptCauseAsync(
                input.OutputId,
                input.BlockIndex,
                ActivityExecutionContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : new LastAttemptCause(row.Attempt, row.Cause);
    }
}
