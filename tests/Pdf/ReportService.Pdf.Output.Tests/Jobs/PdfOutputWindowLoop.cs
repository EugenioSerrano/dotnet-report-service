using System.Text;
using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// Drives a window decision through the same trajectory the PDF output workflow follows:
/// a scheduled block becomes outstanding and advances the next index, a wait completes the
/// oldest outstanding block, and a continuation starts a new run that carries only the next
/// index. It lets a test observe what the loop actually reaches, not a single decision.
/// </summary>
internal static class PdfOutputWindowLoop
{
    /// <summary>The most decisions one chain may take before the harness reports a runaway loop.</summary>
    private const int StepLimit = 1_000_000;

    /// <summary>Runs one run segment from <paramref name="nextIndex"/> until it continues as new or completes.</summary>
    /// <param name="decide">The decision under test.</param>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    /// <param name="nextIndex">The next index the segment starts from.</param>
    /// <param name="window">The window.</param>
    /// <returns>How the segment ended.</returns>
    public static PdfOutputWindowSegment RunSegment(
        Func<int, int, int, int, int, PdfOutputWindowDecision> decide,
        int blockCount,
        int nextIndex,
        int window) =>
        RunChain(decide, blockCount, window, nextIndex, maximumSegments: 1).Segments[0];

    /// <summary>Runs the segments of one output until the chain completes or the segment limit is reached.</summary>
    /// <param name="decide">The decision under test.</param>
    /// <param name="blockCount">How many blocks the partition holds.</param>
    /// <param name="window">The window.</param>
    /// <param name="startIndex">The next index the first segment starts from.</param>
    /// <param name="maximumSegments">The most segments to run.</param>
    /// <returns>The segments, the scheduled indices and the decisions as letters.</returns>
    public static PdfOutputWindowChain RunChain(
        Func<int, int, int, int, int, PdfOutputWindowDecision> decide,
        int blockCount,
        int window,
        int startIndex = 0,
        int maximumSegments = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(decide);
        var segments = new List<PdfOutputWindowSegment>();
        var scheduled = new List<int>();
        var trace = new StringBuilder();
        var next = startIndex;
        var steps = 0;

        while (segments.Count < maximumSegments)
        {
            var first = next;
            var completed = 0;
            var outstanding = new Queue<int>();
            PdfOutputWindowAction end;
            while (true)
            {
                if (++steps > StepLimit)
                {
                    throw new InvalidOperationException("the window loop did not end.");
                }

                var decision = decide(blockCount, next, completed, outstanding.Count, window);
                if (decision.Action == PdfOutputWindowAction.Schedule)
                {
                    var index = decision.NextIndex!.Value;
                    outstanding.Enqueue(index);
                    scheduled.Add(index);
                    next = index + 1;
                    trace.Append('S');
                }
                else if (decision.Action == PdfOutputWindowAction.Wait)
                {
                    if (outstanding.Count == 0)
                    {
                        throw new InvalidOperationException("the window waited with nothing outstanding.");
                    }

                    outstanding.Dequeue();
                    completed++;
                    trace.Append('W');
                }
                else
                {
                    end = decision.Action;
                    trace.Append(end == PdfOutputWindowAction.ContinueAsNew ? 'N' : 'C');
                    break;
                }
            }

            segments.Add(new PdfOutputWindowSegment(first, end, completed, outstanding.Count, next));
            if (end != PdfOutputWindowAction.ContinueAsNew)
            {
                break;
            }
        }

        return new PdfOutputWindowChain(segments, scheduled, trace.ToString());
    }
}

/// <summary>How one run segment ended.</summary>
/// <param name="FirstIndex">The next index the segment started from.</param>
/// <param name="End">Continue as new, or complete.</param>
/// <param name="Completed">The blocks the segment completed.</param>
/// <param name="Outstanding">The blocks still outstanding when it ended.</param>
/// <param name="NextIndex">The next index it carried forward.</param>
internal sealed record PdfOutputWindowSegment(
    int FirstIndex,
    PdfOutputWindowAction End,
    int Completed,
    int Outstanding,
    int NextIndex);

/// <summary>Every segment of one output, the indices scheduled in order, and the decisions as letters.</summary>
/// <param name="Segments">The run segments in order.</param>
/// <param name="Scheduled">Every scheduled index, in scheduling order, across segments.</param>
/// <param name="Trace">S schedule, W wait, N continue as new, C complete.</param>
internal sealed record PdfOutputWindowChain(
    IReadOnlyList<PdfOutputWindowSegment> Segments,
    IReadOnlyList<int> Scheduled,
    string Trace);
