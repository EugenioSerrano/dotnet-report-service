using ReportService.Workers.Orchestration.Jobs;

namespace ReportService.Workers.Orchestration.Tests.Jobs;

/// <summary>
/// The pure window decision of a multi-block PDF output: it never plans more than
/// W outstanding, plans every index exactly once, waits while the window is full,
/// completes when every block was awaited, and continues as a new run at the
/// recorded threshold.
/// </summary>
public sealed class PdfOutputWindowTests
{
    [Fact]
    public void ItSchedulesUpToTheWindowAndThenWaits()
    {
        var first = PdfOutputWindow.Decide(blockCount: 5, nextIndex: 0, completed: 0, outstanding: 0, window: 2);
        Assert.Equal(PdfOutputWindowAction.Schedule, first.Action);
        Assert.Equal(0, first.NextIndex);

        var second = PdfOutputWindow.Decide(5, 1, 0, 1, 2);
        Assert.Equal(PdfOutputWindowAction.Schedule, second.Action);
        Assert.Equal(1, second.NextIndex);

        var full = PdfOutputWindow.Decide(5, 2, 0, 2, 2);
        Assert.Equal(PdfOutputWindowAction.Wait, full.Action);
        Assert.Null(full.NextIndex);
    }

    [Fact]
    public void ItPlansEveryIndexExactlyOnce()
    {
        var planned = new List<int>();
        var next = 0;
        var outstanding = 0;
        var completed = 0;
        while (true)
        {
            var decision = PdfOutputWindow.Decide(11, next, completed, outstanding, 3);
            if (decision.Action == PdfOutputWindowAction.Schedule)
            {
                planned.Add(decision.NextIndex!.Value);
                next = decision.NextIndex.Value + 1;
                outstanding++;
            }
            else if (decision.Action == PdfOutputWindowAction.Wait)
            {
                if (outstanding == 0)
                {
                    break;
                }

                outstanding--;
                completed++;
            }
            else
            {
                break;
            }
        }

        Assert.Equal(Enumerable.Range(0, 11), planned);
        Assert.DoesNotContain(planned.GroupBy(index => index), group => group.Count() > 1);
    }

    [Fact]
    public void ItCompletesOnlyWhenEveryBlockWasAwaited()
    {
        Assert.Equal(PdfOutputWindowAction.Wait, PdfOutputWindow.Decide(3, 3, 2, 1, 2).Action);
        Assert.Equal(PdfOutputWindowAction.Complete, PdfOutputWindow.Decide(3, 3, 3, 0, 2).Action);
    }

    [Fact]
    public void ItContinuesAsNewAtTheThresholdOnlyWithNothingOutstanding()
    {
        Assert.Equal(
            PdfOutputWindowAction.Wait,
            PdfOutputWindow.Decide(2_500, 1_000, PdfOutputWindow.ContinueAsNewAfter, 2, 2).Action);

        var decision = PdfOutputWindow.Decide(2_500, 1_000, PdfOutputWindow.ContinueAsNewAfter, 0, 2);
        Assert.Equal(PdfOutputWindowAction.ContinueAsNew, decision.Action);
        Assert.Null(decision.NextIndex);
    }

    /// <summary>
    /// The loop the workflow runs, not one decision: with two renders in flight, a run that
    /// has completed its share continues as a new run with exactly one thousand blocks done,
    /// nothing outstanding and the next index carried.
    /// </summary>
    [Fact]
    public void AFullWindowRunContinuesAsNewAfterExactlyOneThousandBlocks()
    {
        var segment = PdfOutputWindowLoop.RunSegment(PdfOutputWindow.Decide, 2_500, 0, 2);

        Assert.Equal(PdfOutputWindowAction.ContinueAsNew, segment.End);
        Assert.Equal(1_000, segment.Completed);
        Assert.Equal(0, segment.Outstanding);
        Assert.Equal(1_000, segment.NextIndex);
    }

    /// <summary>
    /// Across every run of one output, at any window: each run but the last completes exactly
    /// one thousand blocks and ends with nothing outstanding, the runs number the share count
    /// rounded up, the last run completes rather than continues, and every index is scheduled
    /// exactly once, each run starting from the index its predecessor carried.
    /// </summary>
    [Theory]
    [MemberData(nameof(WindowsAndBlockCounts))]
    public void EveryRunCompletesItsShareAndEveryBlockIsScheduledOnce(int window, int blockCount)
    {
        var chain = PdfOutputWindowLoop.RunChain(PdfOutputWindow.Decide, blockCount, window);

        var expectedRuns = Math.Max(1, (blockCount + PdfOutputWindow.ContinueAsNewAfter - 1) / PdfOutputWindow.ContinueAsNewAfter);
        Assert.Equal(expectedRuns, chain.Segments.Count);
        for (var run = 0; run < chain.Segments.Count; run++)
        {
            var segment = chain.Segments[run];
            var last = run == chain.Segments.Count - 1;
            Assert.Equal(0, segment.Outstanding);
            Assert.Equal(last ? PdfOutputWindowAction.Complete : PdfOutputWindowAction.ContinueAsNew, segment.End);
            Assert.Equal(
                last ? blockCount - (run * PdfOutputWindow.ContinueAsNewAfter) : PdfOutputWindow.ContinueAsNewAfter,
                segment.Completed);
            Assert.Equal(run * PdfOutputWindow.ContinueAsNewAfter, segment.FirstIndex);
            Assert.Equal(segment.FirstIndex + segment.Completed, segment.NextIndex);
            if (run > 0)
            {
                Assert.Equal(chain.Segments[run - 1].NextIndex, segment.FirstIndex);
            }
        }

        Assert.Equal(Enumerable.Range(0, blockCount), chain.Scheduled);
    }

    /// <summary>The windows and block counts the run theory covers.</summary>
    public static TheoryData<int, int> WindowsAndBlockCounts
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var window in new[] { 1, 2, 3, 64 })
            {
                foreach (var blockCount in new[] { 1, 999, 1_000, 1_001, 2_500, 3_001 })
                {
                    data.Add(window, blockCount);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// A run recorded before the drain replays through the legacy decisions, so they must be
    /// exactly the decisions the window made then, as the committed transcript holds them.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranscriptCases))]
    public void TheLegacyDecisionsAreTheRecordedOnes(int window, int blockCount)
    {
        var recorded = PdfOutputWindowTranscript.Read()[PdfOutputWindowTranscript.KeyOf(window, blockCount)];

        var chain = PdfOutputWindowLoop.RunChain(
            (blocks, next, completed, outstanding, size) =>
                PdfOutputWindow.Decide(blocks, next, completed, outstanding, size, drainBeforeContinueAsNew: false),
            blockCount,
            window);

        Assert.Equal(recorded, chain.Trace);
    }

    /// <summary>The cases the transcript holds.</summary>
    public static TheoryData<int, int> TranscriptCases
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var window in PdfOutputWindowTranscript.Windows)
            {
                foreach (var blockCount in PdfOutputWindowTranscript.BlockCounts)
                {
                    data.Add(window, blockCount);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// States the loop reaches: a run whose share is scheduled but not all completed waits even
    /// with a free slot, and a run one block short of its share still schedules.
    /// </summary>
    [Fact]
    public void ARunWithItsShareScheduledWaitsEvenWithAFreeSlot()
    {
        Assert.Equal(PdfOutputWindowAction.Wait, PdfOutputWindow.Decide(2_500, 1_000, 999, 1, 2).Action);

        var shortOfTheShare = PdfOutputWindow.Decide(2_500, 999, 998, 1, 2);
        Assert.Equal(PdfOutputWindowAction.Schedule, shortOfTheShare.Action);
        Assert.Equal(999, shortOfTheShare.NextIndex);
    }

    /// <summary>When the share ends on the last block, the run completes; it never continues into nothing.</summary>
    [Fact]
    public void AShareEndingOnTheLastBlockCompletes()
    {
        Assert.Equal(PdfOutputWindowAction.Wait, PdfOutputWindow.Decide(1_000, 1_000, 999, 1, 2).Action);
        Assert.Equal(PdfOutputWindowAction.Complete, PdfOutputWindow.Decide(1_000, 1_000, 1_000, 0, 2).Action);
    }

    [Fact]
    public void TheThresholdIsOneThousandBlocks()
    {
        Assert.Equal(1_000, PdfOutputWindow.ContinueAsNewAfter);
        Assert.Equal(
            PdfOutputWindowAction.Schedule,
            PdfOutputWindow.Decide(2_000, 1_000, 999, 0, 2).Action);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, 2)]
    [InlineData(1, -1, 0, 0, 2)]
    [InlineData(1, 0, -1, 0, 2)]
    [InlineData(1, 0, 0, -1, 2)]
    [InlineData(1, 0, 0, 0, 0)]
    public void InvalidInputsAreRefused(int blockCount, int nextIndex, int completed, int outstanding, int window) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOutputWindow.Decide(blockCount, nextIndex, completed, outstanding, window));
}
