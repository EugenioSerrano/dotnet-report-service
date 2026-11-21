using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>
/// Row coverage: the block a page was composed from holds exactly the records the
/// partition says it holds, the composition holds exactly one row for each of them
/// in that order, and the printed document consumed them in that order. The expected
/// values come from the index page, the partition record and the block document -
/// never from the composer's own count - so a composer that omits, repeats or
/// reorders a row cannot make its own text look right.
/// </summary>
public sealed class RowCoverageCheck
{
    /// <summary>Checks the block against the partition and the composition, before anything is printed.</summary>
    /// <param name="input">The partition, the index page, the block and the composition.</param>
    public SourceCheckOutcome CheckSource(SourceCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var partition = input.Partition;
        var index = input.Index;
        var block = input.Block;

        if (index.BlockIndex != block.BlockIndex)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.RowOrder, block.FirstRecordOrdinal);
        }

        // The index page is the partition's own map of its blocks: for the single block
        // this increment writes, its count is the partition's count.
        if (partition.BlockCount == 1 && index.RecordCount != partition.RecordCount)
        {
            return SourceCheckOutcome.Fail(
                index.RecordCount < partition.RecordCount ? VerificationReasons.RowLost : VerificationReasons.RowExtra,
                block.FirstRecordOrdinal);
        }

        if (block.BlockIndex == 0 && index.FirstRecordOrdinal != 1)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.RowOrder, block.FirstRecordOrdinal);
        }

        var blockCount = CheckInterval(
            block.RecordOrdinals,
            index.FirstRecordOrdinal,
            index.RecordCount,
            block.FirstRecordOrdinal);
        if (!blockCount.Passed)
        {
            return blockCount;
        }

        var rows = input.Composition.Rows
            .Select(fragment => fragment.RecordOrdinal)
            .ToList();
        var composed = CheckInterval(rows, index.FirstRecordOrdinal, index.RecordCount, block.FirstRecordOrdinal);
        if (!composed.Passed)
        {
            return composed;
        }

        return SourceCheckOutcome.Pass;
    }

    /// <summary>
    /// Checks that the printed document consumed every row's text, in the order the
    /// composition holds the rows. A row none of whose text was printed is a record
    /// the consumer never receives.
    /// </summary>
    /// <param name="composition">The composed block and the map of its fragments.</param>
    /// <param name="text">The text units of the composition.</param>
    /// <param name="consumed">How many characters of each unit the alignment consumed.</param>
    public SourceCheckOutcome CheckConsumed(ComposedBlock composition, ExtractedText text, IReadOnlyList<int> consumed)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(consumed);

        for (var fragmentIndex = 0; fragmentIndex < composition.Fragments.Count; fragmentIndex++)
        {
            var fragment = composition.Fragments[fragmentIndex];
            if (fragment.Kind != FragmentKind.Row)
            {
                continue;
            }

            var found = false;
            for (var index = 0; index < text.Units.Count; index++)
            {
                var unit = text.Units[index];
                if (unit.FragmentIndex != fragmentIndex)
                {
                    continue;
                }

                found = true;
                if (index >= consumed.Count || consumed[index] < unit.Normalised.Length)
                {
                    return SourceCheckOutcome.Fail(VerificationReasons.RowLost, fragment.RecordOrdinal);
                }
            }

            if (!found)
            {
                return SourceCheckOutcome.Fail(VerificationReasons.RowLost, fragment.RecordOrdinal);
            }
        }

        return SourceCheckOutcome.Pass;
    }

    /// <summary>
    /// Compares a sequence of ordinals with the interval the index page declares: a
    /// sequence that is the interval's own set out of order is a row order defect, a
    /// missing ordinal is a lost record and a repeated one an extra record.
    /// </summary>
    private static SourceCheckOutcome CheckInterval(
        IReadOnlyList<long> ordinals,
        int firstOrdinal,
        long count,
        long failureOrdinal)
    {
        if (ordinals.Count < count)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.RowLost, ordinals.Count > 0 ? ordinals[^1] : failureOrdinal);
        }

        if (ordinals.Count > count)
        {
            return SourceCheckOutcome.Fail(VerificationReasons.RowExtra, ordinals[(int)count]);
        }

        var ordered = ordinals.Order().ToList();
        var isTheInterval = true;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index] != firstOrdinal + index)
            {
                isTheInterval = false;
                break;
            }
        }

        if (isTheInterval)
        {
            for (var index = 0; index < ordinals.Count; index++)
            {
                if (ordinals[index] != firstOrdinal + index)
                {
                    return SourceCheckOutcome.Fail(VerificationReasons.RowOrder, ordinals[index]);
                }
            }

            return SourceCheckOutcome.Pass;
        }

        for (var index = 0; index < ordinals.Count; index++)
        {
            var expected = firstOrdinal + index;
            if (ordinals[index] == expected)
            {
                continue;
            }

            return SourceCheckOutcome.Fail(
                ordinals[index] < expected ? VerificationReasons.RowExtra : VerificationReasons.RowLost,
                ordinals[index]);
        }

        return SourceCheckOutcome.Pass;
    }
}
