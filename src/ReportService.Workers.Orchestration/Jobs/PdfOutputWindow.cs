namespace ReportService.Workers.Orchestration.Jobs;

/// <summary>What the bounded window decided to do next.</summary>
public enum PdfOutputWindowAction
{
    /// <summary>Schedule the block <see cref="PdfOutputWindowDecision.NextIndex"/>.</summary>
    Schedule,

    /// <summary>Wait for one outstanding block to finish.</summary>
    Wait,

    /// <summary>The run has completed its share of blocks and continues as a new run.</summary>
    ContinueAsNew,

    /// <summary>Every block is committed; the output may assemble.</summary>
    Complete,
}

/// <summary>The pure decision of the bounded window.</summary>
/// <param name="Action">What to do next.</param>
/// <param name="NextIndex">The block to schedule, when the action is <see cref="PdfOutputWindowAction.Schedule"/>.</param>
public sealed record PdfOutputWindowDecision(PdfOutputWindowAction Action, int? NextIndex);

/// <summary>
/// The pure scheduling decision of a multi-block PDF output: how many block
/// renders may be outstanding, which block index is next, and when the run has
/// completed its share and continues as a new one. It holds no clock, no random
/// value and no I/O, so a hermetic test proves the whole decision without an
/// engine, and the workflow that uses it stays replay-deterministic.
/// </summary>
public static class PdfOutputWindow
{
    /// <summary>How many completed block activities one run may take before it continues as a new run.</summary>
    public const int ContinueAsNewAfter = 1_000;

    /// <summary>
    /// Decides the next step of the window. Once a run has scheduled its share of
    /// <see cref="ContinueAsNewAfter"/> blocks and blocks remain, it schedules nothing more,
    /// waits for every outstanding render, and then continues as a new run, so every run but
    /// the last completes exactly its share whatever the window.
    /// </summary>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    /// <param name="nextIndex">The next block index not yet scheduled.</param>
    /// <param name="completed">How many blocks this run has completed.</param>
    /// <param name="outstanding">How many renders this run has scheduled and not awaited.</param>
    /// <param name="window">The most renders allowed outstanding at once.</param>
    /// <returns>The decision.</returns>
    public static PdfOutputWindowDecision Decide(
        int blockCount,
        int nextIndex,
        int completed,
        int outstanding,
        int window) =>
        Decide(blockCount, nextIndex, completed, outstanding, window, drainBeforeContinueAsNew: true);

    /// <summary>Decides the next step of the window, with or without the drain before a continuation.</summary>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    /// <param name="nextIndex">The next block index not yet scheduled.</param>
    /// <param name="completed">How many blocks this run has completed.</param>
    /// <param name="outstanding">How many renders this run has scheduled and not awaited.</param>
    /// <param name="window">The most renders allowed outstanding at once.</param>
    /// <param name="drainBeforeContinueAsNew">
    /// <see langword="true"/> for the current decision; <see langword="false"/> for the
    /// decision a run recorded before the drain existed made, which continued as new only when
    /// it happened to have nothing outstanding past the threshold. Such a run replays through it unchanged.
    /// </param>
    /// <returns>The decision.</returns>
    public static PdfOutputWindowDecision Decide(
        int blockCount,
        int nextIndex,
        int completed,
        int outstanding,
        int window,
        bool drainBeforeContinueAsNew)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockCount);
        ArgumentOutOfRangeException.ThrowIfNegative(nextIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfNegative(outstanding);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);

        if (drainBeforeContinueAsNew)
        {
            if (nextIndex < blockCount && completed + outstanding >= ContinueAsNewAfter)
            {
                return new PdfOutputWindowDecision(
                    outstanding > 0 ? PdfOutputWindowAction.Wait : PdfOutputWindowAction.ContinueAsNew,
                    null);
            }
        }
        else if (completed >= ContinueAsNewAfter && outstanding == 0 && nextIndex < blockCount)
        {
            return new PdfOutputWindowDecision(PdfOutputWindowAction.ContinueAsNew, null);
        }

        if (nextIndex >= blockCount)
        {
            return new PdfOutputWindowDecision(
                outstanding > 0 ? PdfOutputWindowAction.Wait : PdfOutputWindowAction.Complete,
                null);
        }

        return outstanding >= window
            ? new PdfOutputWindowDecision(PdfOutputWindowAction.Wait, null)
            : new PdfOutputWindowDecision(PdfOutputWindowAction.Schedule, nextIndex);
    }
}
