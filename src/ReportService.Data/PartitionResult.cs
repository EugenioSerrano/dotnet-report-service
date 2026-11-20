namespace ReportService.Data;

/// <summary>What one partitioning pass admitted: one block holding this interval of records.</summary>
/// <param name="BlockCount">How many blocks the pass wrote; one in this increment.</param>
/// <param name="RecordCount">How many records the block holds.</param>
/// <param name="RecordBytes">How many bytes of record content the block holds.</param>
/// <param name="FirstRecordOrdinal">The ordinal of the block's first record; 1 for the first block.</param>
public sealed record PartitionSummary(int BlockCount, long RecordCount, long RecordBytes, int FirstRecordOrdinal);

/// <summary>
/// The result of the data step, as the workflow receives it: what the partition holds
/// and where its two objects live. It carries identifiers, counts, sizes and hashes
/// only, never content. A PDF output the pass refused by the row bound travels with it,
/// so a step that runs again answers the refusal its partition record already holds instead
/// of reading the snapshot for it.
/// </summary>
/// <param name="PartitionerVersion">The version of the rules the partition was made with.</param>
/// <param name="BlockCount">How many blocks the partition holds.</param>
/// <param name="RecordCount">How many records the partition holds.</param>
/// <param name="RecordBytes">How many bytes of record content the partition holds.</param>
/// <param name="FirstRecordOrdinal">The ordinal of the first block's first record.</param>
/// <param name="BlockKey">The key the block document was written under.</param>
/// <param name="BlockSizeBytes">The size of the block document.</param>
/// <param name="BlockSha256">The SHA-256 of the block document, lower-case hexadecimal.</param>
/// <param name="IndexKey">The key the index page was written under.</param>
/// <param name="IndexSizeBytes">The size of the index page.</param>
/// <param name="IndexSha256">The SHA-256 of the index page, lower-case hexadecimal.</param>
/// <param name="PdfRefusal">The PDF output's refusal, when the pass refused it; null otherwise.</param>
public sealed record PartitionResult(
    int PartitionerVersion,
    int BlockCount,
    long RecordCount,
    long RecordBytes,
    int FirstRecordOrdinal,
    string BlockKey,
    long BlockSizeBytes,
    string BlockSha256,
    string IndexKey,
    long IndexSizeBytes,
    string IndexSha256,
    PdfOutputRefusal? PdfRefusal = null);

/// <summary>
/// Why a PDF output was refused before anything was rendered for it: the code it ends with and
/// the stored diagnostic that says which record and field to fix. It carries a diagnostic and
/// never a value of the document.
/// </summary>
/// <param name="Code">The code the output ends with.</param>
/// <param name="Reason">The stored reason, as the output's row carries it.</param>
public sealed record PdfOutputRefusal(string Code, string Reason);
