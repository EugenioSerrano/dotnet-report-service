using ReportService.Templates;

namespace ReportService.Pdf.Verification;

/// <summary>What the job's partition record says about the block being rendered.</summary>
/// <param name="BlockCount">How many blocks the partition holds.</param>
/// <param name="RecordCount">How many records the partition holds.</param>
public sealed record PartitionFacts(int BlockCount, long RecordCount);

/// <summary>What the partition's index page says about one block.</summary>
/// <param name="BlockIndex">The block's position in the partition, from zero.</param>
/// <param name="RecordCount">How many records the block holds.</param>
/// <param name="FirstRecordOrdinal">The ordinal of the block's first record.</param>
public sealed record IndexFacts(int BlockIndex, long RecordCount, int FirstRecordOrdinal);

/// <summary>One record of the block: its ordinal and its values as plain values.</summary>
/// <param name="Ordinal">The record's ordinal in the snapshot.</param>
/// <param name="Values">The record's members, as the render context exposes them.</param>
public sealed record BlockRecord(long Ordinal, IReadOnlyDictionary<string, object?> Values);

/// <summary>What the block document holds, read back with the same reader that wrote it.</summary>
/// <param name="BlockIndex">The block's position in the partition, from zero.</param>
/// <param name="FirstRecordOrdinal">The ordinal of its first record.</param>
/// <param name="Records">The records it holds, in order.</param>
/// <param name="Header">The header as received.</param>
public sealed record BlockFacts(
    int BlockIndex,
    int FirstRecordOrdinal,
    IReadOnlyList<BlockRecord> Records,
    IReadOnlyDictionary<string, object?> Header)
{
    /// <summary>The ordinal of each record, in order.</summary>
    public IReadOnlyList<long> RecordOrdinals => Records.Select(record => record.Ordinal).ToList();
}

/// <summary>Everything the source checks read before a single page is printed.</summary>
/// <param name="Partition">The job's partition record.</param>
/// <param name="Index">The index page's entry for this block.</param>
/// <param name="Block">The block document, read back.</param>
/// <param name="Composition">The composed block and the map of its fragments.</param>
/// <param name="Text">The text units of the composition.</param>
/// <param name="Anchors">The fields the package prints as they are, or <see langword="null"/> when it declares none.</param>
/// <param name="IsFirstBlock">Whether this is the first block of the job.</param>
public sealed record SourceCheckInput(
    PartitionFacts Partition,
    IndexFacts Index,
    BlockFacts Block,
    ComposedBlock Composition,
    ExtractedText Text,
    VerificationAnchors? Anchors,
    bool IsFirstBlock);

/// <summary>What a source check found. A failure names its reason and where it stopped, never a value.</summary>
/// <param name="Passed">Whether the check passed.</param>
/// <param name="Reason">The reason word, or <see langword="null"/> when it passed.</param>
/// <param name="RecordOrdinal">The record the failure was found at, or 0 for a document part.</param>
/// <param name="AnchorPath">The field path of an anchor failure, or <see langword="null"/>.</param>
public sealed record SourceCheckOutcome(bool Passed, string? Reason, long? RecordOrdinal, string? AnchorPath)
{
    /// <summary>A check that passed.</summary>
    public static SourceCheckOutcome Pass { get; } = new(true, null, null, null);

    /// <summary>A check that failed with <paramref name="reason"/>.</summary>
    /// <param name="reason">The reason word.</param>
    /// <param name="recordOrdinal">The record the failure was found at.</param>
    /// <param name="anchorPath">The field path of an anchor failure, when one applies.</param>
    public static SourceCheckOutcome Fail(string reason, long? recordOrdinal = null, string? anchorPath = null) =>
        new(false, reason, recordOrdinal, anchorPath);
}

/// <summary>Everything the printed document is checked against after it was printed.</summary>
/// <param name="PdfPath">The file of the printed document.</param>
/// <param name="BlockIndex">The block's position in the partition, from zero.</param>
/// <param name="Margins">The template's declared margins, in points.</param>
/// <param name="Composition">The composed block the document was laid out from.</param>
/// <param name="Text">The text units of the composition.</param>
/// <param name="SourceChecksMs">How long the source checks took.</param>
public sealed record PrintedCheckInput(
    string PdfPath,
    int BlockIndex,
    PageMargins Margins,
    ComposedBlock Composition,
    ExtractedText Text,
    long SourceChecksMs);

/// <summary>
/// What one block's verification found: whether it passed, and when it did not, a
/// reason and the diagnostic position - page, unit, offset, record, field path - and
/// never a value. The two timings are the ones the acceptance thresholds measure.
/// </summary>
/// <param name="Passed">Whether every check passed.</param>
/// <param name="Reason">The reason word of the first failure, or <see langword="null"/>.</param>
/// <param name="BlockIndex">The block the verification ran on.</param>
/// <param name="RecordOrdinal">The record of the failure, or 0 for a document part.</param>
/// <param name="AnchorPath">The field path of an anchor failure.</param>
/// <param name="Page">The page of the failure, from one.</param>
/// <param name="UnitIndex">The unit of the failure.</param>
/// <param name="Offset">The offset within the page of the failure.</param>
/// <param name="Pages">How many pages the document holds.</param>
/// <param name="Words">How many words the document holds.</param>
/// <param name="ReadAndAlignMs">How long reading the document and aligning it took.</param>
/// <param name="SourceChecksMs">How long the source checks took.</param>
public sealed record VerificationResult(
    bool Passed,
    string? Reason,
    int BlockIndex,
    long? RecordOrdinal,
    string? AnchorPath,
    int? Page,
    int? UnitIndex,
    int? Offset,
    int Pages,
    int Words,
    long ReadAndAlignMs,
    long SourceChecksMs)
{
    /// <summary>A failure of the printed document.</summary>
    /// <param name="reason">The reason word.</param>
    /// <param name="blockIndex">The block the verification ran on.</param>
    /// <param name="page">The page of the failure.</param>
    /// <param name="unitIndex">The unit of the failure.</param>
    /// <param name="offset">The offset within the page.</param>
    /// <param name="pages">How many pages the document holds.</param>
    /// <param name="words">How many words the document holds.</param>
    /// <param name="readAndAlignMs">How long reading and aligning took.</param>
    /// <param name="sourceChecksMs">How long the source checks took.</param>
    public static VerificationResult Failed(
        string reason,
        int blockIndex,
        int? page,
        int? unitIndex,
        int? offset,
        int pages,
        int words,
        long readAndAlignMs,
        long sourceChecksMs) =>
        new(false, reason, blockIndex, null, null, page, unitIndex, offset, pages, words, readAndAlignMs, sourceChecksMs);
}
