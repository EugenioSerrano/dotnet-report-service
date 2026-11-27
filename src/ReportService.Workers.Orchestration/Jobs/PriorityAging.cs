using ReportService.Jobs;

namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What one aging sample decided.</summary>
public enum PriorityAgingAction
{
    /// <summary>The output keeps sampling.</summary>
    SampleAgain,

    /// <summary>The output's pending time reached the wait: the job is promoted to normal.</summary>
    Promote,

    /// <summary>The job is already normal (another output promoted it): the output adopts it, without counting.</summary>
    Adopt,

    /// <summary>The output samples no more: it is not low, has no wait, or has no work left.</summary>
    Stop,
}

/// <summary>The decision of one sample and the pending time it leaves.</summary>
/// <param name="Action">What to do.</param>
/// <param name="PendingSeconds">The output's pending time after this sample.</param>
public sealed record PriorityAgingDecision(PriorityAgingAction Action, double PendingSeconds);

/// <summary>
/// The pure decisions of the aging rule: a low job's output samples its own pending work at a
/// fixed period, adds the period to its pending time when work was pending, and promotes the
/// job to normal for its later dispatch once that time reaches the deployment's wait. Nothing
/// here reads a clock or does I/O, so the workflow that uses it replays exactly.
/// </summary>
public static class PriorityAging
{
    /// <summary>How many samples one run takes before it drains and continues as new.</summary>
    public const int SamplesBeforeContinueAsNew = 1_000;

    /// <summary>The shortest sample period.</summary>
    public static readonly TimeSpan MinimumPeriod = TimeSpan.FromSeconds(5);

    /// <summary>The longest sample period.</summary>
    public static readonly TimeSpan MaximumPeriod = TimeSpan.FromSeconds(60);

    /// <summary>The sample period for a wait: a twentieth of it, between 5 s and 60 s.</summary>
    /// <param name="wait">The deployment's aging wait.</param>
    public static TimeSpan SamplePeriod(TimeSpan wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero);
        var period = wait / 20;
        if (period < MinimumPeriod)
        {
            return MinimumPeriod;
        }

        return period > MaximumPeriod ? MaximumPeriod : period;
    }

    /// <summary>Whether an output samples at all: only a low output with a wait.</summary>
    /// <param name="outputClass">The class the output dispatches on.</param>
    /// <param name="wait">The deployment's aging wait, when the output carries one.</param>
    public static bool Samples(JobPriority outputClass, TimeSpan? wait) =>
        outputClass == JobPriority.Low && wait is { } value && value > TimeSpan.Zero;

    /// <summary>Decides one sample.</summary>
    /// <param name="outputClass">The class the output dispatches on.</param>
    /// <param name="wait">The deployment's aging wait, when the output carries one.</param>
    /// <param name="pendingSeconds">The output's pending time before this sample.</param>
    /// <param name="samplePending">Whether the sample saw work scheduled and not started.</param>
    /// <param name="jobClass">The job's effective class as the store reads it, or none.</param>
    /// <param name="workRemaining">Whether the output has work not yet completed.</param>
    public static PriorityAgingDecision Decide(
        JobPriority outputClass,
        TimeSpan? wait,
        double pendingSeconds,
        bool samplePending,
        JobPriority? jobClass,
        bool workRemaining)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pendingSeconds);
        if (!Samples(outputClass, wait) || !workRemaining)
        {
            return new PriorityAgingDecision(PriorityAgingAction.Stop, pendingSeconds);
        }

        if (jobClass is { } stored && stored != JobPriority.Low)
        {
            return new PriorityAgingDecision(PriorityAgingAction.Adopt, pendingSeconds);
        }

        var pending = samplePending ? pendingSeconds + SamplePeriod(wait!.Value).TotalSeconds : pendingSeconds;
        return pending >= wait!.Value.TotalSeconds
            ? new PriorityAgingDecision(PriorityAgingAction.Promote, pending)
            : new PriorityAgingDecision(PriorityAgingAction.SampleAgain, pending);
    }

    /// <summary>
    /// The window's decision with the sample cap applied: once a run has taken
    /// <see cref="SamplesBeforeContinueAsNew"/> samples and blocks remain, it drains - waits while
    /// renders are outstanding - and then continues as new, the same path the window takes after
    /// its own share of blocks. Below the cap the window's decision stands unchanged.
    /// </summary>
    /// <param name="decision">What the window decided.</param>
    /// <param name="samplesInRun">How many samples this run has taken.</param>
    /// <param name="outstanding">How many renders this run has scheduled and not awaited.</param>
    /// <param name="nextIndex">The next block index not yet scheduled.</param>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    public static PdfOutputWindowDecision Window(
        PdfOutputWindowDecision decision,
        int samplesInRun,
        int outstanding,
        int nextIndex,
        int blockCount)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentOutOfRangeException.ThrowIfNegative(samplesInRun);
        ArgumentOutOfRangeException.ThrowIfNegative(outstanding);
        if (samplesInRun < SamplesBeforeContinueAsNew || nextIndex >= blockCount)
        {
            return decision;
        }

        return new PdfOutputWindowDecision(
            outstanding > 0 ? PdfOutputWindowAction.Wait : PdfOutputWindowAction.ContinueAsNew,
            null);
    }
}
