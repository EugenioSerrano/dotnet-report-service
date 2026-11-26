using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The block-limit policy of the streaming partitioner: where the soft target is
/// reached (cut at the next group boundary) and where the hard maximum forces a cut.
/// The expected values are ADR-022 §3.6's — soft target 1 000 records or 512 KiB of
/// record bytes, hard maximum 2 000 records or 1 MiB, whichever first; a record is
/// never split, and a block holds at least one record. Nothing here is read from the
/// partitioner.
/// </summary>
public sealed class PartitionLimitsTests
{
    private const long SoftBytes = 512L * 1024;   // 524 288
    private const long HardBytes = 1024L * 1024;  // 1 048 576

    [Fact]
    public void TheDefaultLimitsAreTheGovernedSoftAndHardValues()
    {
        var limits = PartitionLimits.Default;

        Assert.Equal(1_000, limits.SoftMaxRecords);
        Assert.Equal(SoftBytes, limits.SoftMaxRecordBytes);
        Assert.Equal(2_000, limits.HardMaxRecords);
        Assert.Equal(HardBytes, limits.HardMaxRecordBytes);
    }

    [Fact]
    public void AnEmptyBlockAdmitsTheNextRecordEvenAtTheHardByteMaximum()
    {
        // A block holds at least one record; a record equal to the hard byte maximum fits alone.
        Assert.Equal(BlockFill.Fits, PartitionLimits.Default.Classify(recordsInBlock: 0, recordBytesInBlock: 0, nextRecordBytes: HardBytes));
    }

    [Fact]
    public void BelowTheSoftTargetTheNextRecordFits()
    {
        Assert.Equal(BlockFill.Fits, PartitionLimits.Default.Classify(999, 0, 10));
        Assert.Equal(BlockFill.Fits, PartitionLimits.Default.Classify(10, SoftBytes - 1, 10));
    }

    [Fact]
    public void AtTheSoftRecordTargetTheBlockWantsToCutAtTheNextBoundary()
    {
        Assert.Equal(BlockFill.SoftReached, PartitionLimits.Default.Classify(1_000, 0, 10));
    }

    [Fact]
    public void AtTheSoftByteTargetTheBlockWantsToCutAtTheNextBoundary()
    {
        Assert.Equal(BlockFill.SoftReached, PartitionLimits.Default.Classify(10, SoftBytes, 10));
    }

    [Fact]
    public void TheSoftTargetIsReachedByWhicheverBoundComesFirst()
    {
        // Records reach the soft target while the bytes are still low.
        Assert.Equal(BlockFill.SoftReached, PartitionLimits.Default.Classify(1_000, 1_024, 10));
        // Bytes reach the soft target while the record count is still low.
        Assert.Equal(BlockFill.SoftReached, PartitionLimits.Default.Classify(10, SoftBytes, 10));
    }

    [Fact]
    public void AtTheHardRecordMaximumTheNextRecordMustStartANewBlock()
    {
        Assert.Equal(BlockFill.MustCut, PartitionLimits.Default.Classify(2_000, 0, 10));
    }

    [Fact]
    public void WhenTheNextRecordWouldCrossTheHardByteMaximumItMustStartANewBlock()
    {
        // The open block is non-empty and already large; the next record crosses the hard byte maximum.
        Assert.Equal(BlockFill.MustCut, PartitionLimits.Default.Classify(1, 1_000_000, 100_000));
    }

    [Fact]
    public void TheHardMaximumIsReachedByWhicheverBoundComesFirst()
    {
        // The record count hits the hard maximum before the bytes do.
        Assert.Equal(BlockFill.MustCut, PartitionLimits.Default.Classify(2_000, 1_024, 10));
        // The bytes hit the hard maximum before the record count does.
        Assert.Equal(BlockFill.MustCut, PartitionLimits.Default.Classify(3, HardBytes, 10));
    }

    [Fact]
    public void ExactlyAtTheHardByteMaximumTheRecordStillFitsTheBlock()
    {
        // Equal to the hard maximum is admitted; one byte beyond it is not.
        Assert.NotEqual(BlockFill.MustCut, PartitionLimits.Default.Classify(1, HardBytes - 100, 100));
        Assert.Equal(BlockFill.MustCut, PartitionLimits.Default.Classify(1, HardBytes - 100, 101));
    }
}
