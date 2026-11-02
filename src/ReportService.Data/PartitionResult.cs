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
/// only, never content.
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
    string IndexSha256);
