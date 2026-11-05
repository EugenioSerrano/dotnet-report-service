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

        var outcome = anchors.Check(input);
        if (outcome.Passed)
        {
            outcome = coverage.CheckSource(input);
        }

        if (outcome.Passed)
        {
            outcome = rowText.Check(input);
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
            reading = PdfWordReader.Read(input.PdfPath, input.Margins);
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

        var pageTexts = reading.Pages
            .Select(PlacementConvention.ContentText)
            .ToList();
        var alignment = aligner.Align(pageTexts, input.Text.Units);
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
