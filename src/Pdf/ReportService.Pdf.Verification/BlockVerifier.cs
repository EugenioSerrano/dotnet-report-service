using System.Diagnostics;

namespace ReportService.Pdf.Verification;

/// <summary>
/// Verifies one printed block: the checks that need no page run before anything is
/// printed - the block against its partition, one row per record, a printed unit per
/// row, every declared field at its value - and a failure there ends the block with
/// nothing printed. The checks that need the page run after it: the document is
/// read, every page must print a word, the composed text must be consumed in
/// painting order, and every row's text must have been printed. The verdict is the
/// conjunction of all of them; no setting turns a check off.
/// </summary>
public sealed class BlockVerifier
{
    private readonly RowCoverageCheck coverage = new();
    private readonly RowTextCheck rowText = new();
    private readonly AnchorCheck anchors = new();
    private readonly PageCheck pages = new();
    private readonly PageGeometryCheck geometry = new();
    private readonly PageAccountingCheck accounting = new();
    private readonly ContentOrderAligner aligner;

    /// <summary>Creates a verifier whose alignment spends at most <paramref name="alignmentBudget"/> comparisons per block.</summary>
    /// <param name="alignmentBudget">The comparison budget of one block's alignment.</param>
    public BlockVerifier(int alignmentBudget = ContentOrderAligner.DefaultBudget)
    {
        aligner = new ContentOrderAligner(alignmentBudget);
    }

    /// <summary>
    /// Runs the checks that need no page. A failure here means nothing is printed and
    /// nothing is written.
    /// </summary>
    /// <param name="input">The partition, the block, the composition and the declared fields.</param>
    /// <param name="sourceChecksMs">How long the checks took, once they end.</param>
    public SourceCheckOutcome CheckSource(SourceCheckInput input, out long sourceChecksMs)
    {
        ArgumentNullException.ThrowIfNull(input);
        var started = Stopwatch.GetTimestamp();

        // The declaration is checked before anything else: a package that prints no field
        // as it is declares nothing the block could be bound to, so it is refused whatever
        // else would have failed too.
        if (input.Anchors is null)
        {
            sourceChecksMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return SourceCheckOutcome.Fail(VerificationReasons.AnchorsUndeclared);
        }

        // A block that continues a group owes its group total the group's earlier rows;
        // the partition records that opening, and a block that lacks it fails closed
        // rather than printing the total as if the group began here.
        if (input.PageAccounting
            && input.Index.ContinuesPreviousGroup
            && input.Index.OpeningGroupAccumulated is null)
        {
            sourceChecksMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return SourceCheckOutcome.Fail(VerificationReasons.AccountingIndexIncomplete);
        }

        var outcome = coverage.CheckSource(input);
        if (outcome.Passed)
        {
            outcome = rowText.Check(input);
        }

        if (outcome.Passed)
        {
            outcome = anchors.Check(input);
        }

        sourceChecksMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return outcome;
    }

    /// <summary>
    /// Reads the printed document and runs the checks that need it. A document the
    /// reader cannot read fails; it is never read as an empty document.
    /// </summary>
    /// <param name="input">The document, the composition and how long the source checks took.</param>
    public VerificationResult Verify(PrintedCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var started = Stopwatch.GetTimestamp();

        PdfReading reading;
        try
        {
            reading = PdfWordReader.Read(input.PdfPath, input.Frame.Margins);
        }
        catch (PdfUnreadableException)
        {
            return VerificationResult.Failed(
                VerificationReasons.Unreadable, input.BlockIndex, null, null, null, 0, 0, Elapsed(started), input.SourceChecksMs);
        }

        var pageCheck = pages.Check(reading);
        if (!pageCheck.Passed)
        {
            return VerificationResult.Failed(
                pageCheck.Reason!, input.BlockIndex, null, null, null, reading.Pages.Count, reading.WordCount, Elapsed(started), input.SourceChecksMs);
        }

        // A page of another size makes every comparison against it unreliable, so the
        // page's own geometry is checked before the text it holds.
        var pageGeometry = geometry.Check(reading, input.Frame);
        if (!pageGeometry.Passed)
        {
            return VerificationResult.Failed(
                VerificationReasons.PageSize,
                input.BlockIndex,
                pageGeometry.Page,
                null,
                null,
                reading.Pages.Count,
                reading.WordCount,
                Elapsed(started),
                input.SourceChecksMs,
                pageGeometry.MeasuredWidthPt,
                pageGeometry.MeasuredHeightPt,
                pageGeometry.ExpectedWidthPt,
                pageGeometry.ExpectedHeightPt);
        }

        foreach (var page in reading.Pages)
        {
            foreach (var word in page.Words)
            {
                if (Normalisation.HasUnmappedGlyph(word.Text))
                {
                    return VerificationResult.Failed(
                        VerificationReasons.UnmappedGlyph,
                        input.BlockIndex,
                        page.Number,
                        null,
                        null,
                        reading.Pages.Count,
                        reading.WordCount,
                        Elapsed(started),
                        input.SourceChecksMs);
                }
            }
        }

        // The accounting cells' units are excluded from the composed-text check (their text
        // is written by the layout and judged by the accounting check): the alignment runs
        // over the other units, and the accounting units are then consumed at their own
        // positions so the row-coverage check sees every unit.
        AlignmentOutcome alignment;
        if (input.Accounting is { } accountingContract)
        {
            var judged = input.Text.Units.Where(unit => !unit.Accounting).ToList();
            alignment = aligner.Align(accounting.PageTexts(reading, accountingContract), judged);
            alignment = ContentOrderAligner.ConsumeAccountingUnits(alignment, input.Text.Units);
        }
        else
        {
            alignment = aligner.Align(
                reading.Pages.Select(PlacementConvention.ContentText).ToList(),
                input.Text.Units);
        }

        if (!alignment.Passed)
        {
            return VerificationResult.Failed(
                alignment.Reason!,
                input.BlockIndex,
                alignment.Page,
                alignment.UnitIndex,
                alignment.Offset,
                reading.Pages.Count,
                reading.WordCount,
                Elapsed(started),
                input.SourceChecksMs);
        }

        var consumed = coverage.CheckConsumed(input.Composition, input.Text, alignment.ConsumedCharacters);
        if (!consumed.Passed)
        {
            return VerificationResult.Failed(
                consumed.Reason!,
                input.BlockIndex,
                null,
                null,
                null,
                reading.Pages.Count,
                reading.WordCount,
                Elapsed(started),
                input.SourceChecksMs);
        }

        if (accounting.Check(input, reading, alignment) is { Passed: false } verdict)
        {
            return VerificationResult.Failed(
                verdict.Reason!,
                input.BlockIndex,
                verdict.Page,
                null,
                null,
                reading.Pages.Count,
                reading.WordCount,
                Elapsed(started),
                input.SourceChecksMs) with
            {
                RecordOrdinal = verdict.RecordOrdinal,
            };
        }

        return new VerificationResult(
            Passed: true,
            Reason: null,
            BlockIndex: input.BlockIndex,
            RecordOrdinal: null,
            AnchorPath: null,
            Page: null,
            UnitIndex: null,
            Offset: null,
            Pages: reading.Pages.Count,
            Words: reading.WordCount,
            ReadAndAlignMs: Elapsed(started),
            SourceChecksMs: input.SourceChecksMs);
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
