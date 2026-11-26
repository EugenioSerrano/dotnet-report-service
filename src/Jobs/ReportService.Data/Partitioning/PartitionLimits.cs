namespace ReportService.Data.Partitioning;

/// <summary>How the open block answers the next record under the block-limit policy.</summary>
public enum BlockFill
{
    /// <summary>The record fits the open block; admit it.</summary>
    Fits,

    /// <summary>The soft target is reached; cut at the next group boundary, otherwise keep admitting.</summary>
    SoftReached,

    /// <summary>The hard maximum would be crossed; the record must start a new block.</summary>
    MustCut,
}

/// <summary>
/// The block-limit policy of the streaming partitioner: a soft target
/// — a record count or a count of record bytes, whichever is reached first — past which
/// the next group boundary is preferred as the cut, and a hard maximum past which the
/// block is cut anyway. A record is never split and a block holds at least one record,
/// so a record as large as the hard byte maximum starts, and fills, a block of its own.
/// </summary>
/// <param name="SoftMaxRecords">The record count at which the block prefers to cut at the next boundary.</param>
/// <param name="SoftMaxRecordBytes">The count of record bytes at which the block prefers to cut at the next boundary.</param>
/// <param name="HardMaxRecords">The most records a block may hold.</param>
/// <param name="HardMaxRecordBytes">The most bytes of record content a block may hold.</param>
public sealed record PartitionLimits(
    int SoftMaxRecords,
    long SoftMaxRecordBytes,
    int HardMaxRecords,
    long HardMaxRecordBytes)
{
    /// <summary>The governed limits: soft 1 000 records or 512 KiB, hard 2 000 records or 1 MiB.</summary>
    public static PartitionLimits Default { get; } = new(1_000, 512L * 1024, 2_000, 1_024L * 1024);

    /// <summary>
    /// Answers whether the next record fits the open block, reaches the soft target, or
    /// must start a new block. An empty block always admits the next record (a block holds
    /// at least one record); the hard maximum is measured against the record that would be
    /// added; the soft target against the bytes and records the block already holds.
    /// </summary>
    /// <param name="recordsInBlock">How many records the open block already holds.</param>
    /// <param name="recordBytesInBlock">How many bytes of record content the open block already holds.</param>
    /// <param name="nextRecordBytes">The wire length of the record being considered.</param>
    public BlockFill Classify(long recordsInBlock, long recordBytesInBlock, long nextRecordBytes)
    {
        if (recordsInBlock == 0)
        {
            return BlockFill.Fits;
        }

        if (recordsInBlock + 1 > HardMaxRecords || recordBytesInBlock + nextRecordBytes > HardMaxRecordBytes)
        {
            return BlockFill.MustCut;
        }

        if (recordsInBlock >= SoftMaxRecords || recordBytesInBlock >= SoftMaxRecordBytes)
        {
            return BlockFill.SoftReached;
        }

        return BlockFill.Fits;
    }
}
