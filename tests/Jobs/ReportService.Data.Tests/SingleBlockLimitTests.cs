using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The effective single-block limit: the configured record count capped by the block's
/// hard maximum, the byte bound that no configuration changes, the boundary of each
/// dimension and the order in which the two are evaluated. The limit carries the
/// applied value and the dimension, never a value of the document.
/// </summary>
public sealed class SingleBlockLimitTests
{
    [Theory]
    [InlineData(1_000, 1_000)]
    [InlineData(2_000, 2_000)]
    [InlineData(5_000, 2_000)]
    public void TheEffectiveRecordCountIsTheConfiguredValueCappedByTheHardMaximum(int configured, int effective)
    {
        var limit = new SingleBlockLimit(configured);

        Assert.Equal(configured, limit.ConfiguredRecords);
        Assert.Equal(effective, limit.EffectiveRecords);
    }

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(2_000, false)]
    [InlineData(5_000, true)]
    public void OnlyAValueAboveTheHardMaximumIsIneffective(int configured, bool ineffective)
    {
        Assert.Equal(ineffective, new SingleBlockLimit(configured).IsConfiguredValueIneffective);
    }

    [Fact]
    public void TheHardMaximumIsTheBlocksOwn()
    {
        Assert.Equal(DataContractLimits.BlockRecords, SingleBlockLimit.HardMaxRecords);
        Assert.Equal(DataContractLimits.BlockRecordBytes, SingleBlockLimit.HardMaxRecordBytes);
        Assert.Equal(SingleBlockLimit.HardMaxRecords, SingleBlockLimit.Unconfigured.ConfiguredRecords);
        Assert.Equal(SingleBlockLimit.HardMaxRecords, SingleBlockLimit.Unconfigured.EffectiveRecords);
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(1_000, false)]
    public void TheRecordAtTheBoundIsAdmittedAndTheOneBeyondItIsRefused(long recordsSoFar, bool admitted)
    {
        var limit = new SingleBlockLimit(1_000);

        var fits = limit.TryAdmit(recordsSoFar, bytesSoFar: 0, nextRecordBytes: 10, out var refusal);

        Assert.Equal(admitted, fits);
        if (!admitted)
        {
            Assert.Equal(BlockLimitDimension.Records, refusal.Dimension);
            Assert.Equal(1_000, refusal.Applied);
            Assert.Equal(1_001, refusal.Ordinal);
        }
    }

    [Theory]
    [InlineData(1_048_575, true)]
    [InlineData(1_048_576, false)]
    public void TheByteBoundIsInclusiveAndTheByteBeyondItIsRefused(long bytesSoFar, bool admitted)
    {
        var limit = new SingleBlockLimit(1_000);

        var fits = limit.TryAdmit(recordsSoFar: 0, bytesSoFar, nextRecordBytes: 1, out var refusal);

        Assert.Equal(admitted, fits);
        if (!admitted)
        {
            Assert.Equal(BlockLimitDimension.Bytes, refusal.Dimension);
            Assert.Equal(SingleBlockLimit.HardMaxRecordBytes, refusal.Applied);
            Assert.Equal(1, refusal.Ordinal);
        }
    }

    [Fact]
    public void ARecordThatCrossesBothBoundsAtOnceIsReportedByTheRecordCount()
    {
        var limit = new SingleBlockLimit(1_000);

        var fits = limit.TryAdmit(
            recordsSoFar: 1_000,
            bytesSoFar: SingleBlockLimit.HardMaxRecordBytes,
            nextRecordBytes: 1_048_576,
            out var refusal);

        Assert.False(fits);
        Assert.Equal(BlockLimitDimension.Records, refusal.Dimension);
    }

    [Fact]
    public void ARecordThatIsLargeButNotPastTheByteBoundIsAdmittedOnAnEmptyBlock()
    {
        var limit = new SingleBlockLimit(1_000);

        // A 900 KiB record after a few small ones: the bytes are what crosses first when
        // another record follows it.
        Assert.True(limit.TryAdmit(recordsSoFar: 0, bytesSoFar: 0, nextRecordBytes: 900 * 1024, out _));
        Assert.False(limit.TryAdmit(recordsSoFar: 1, bytesSoFar: 900 * 1024, nextRecordBytes: 200 * 1024, out var refusal));
        Assert.Equal(BlockLimitDimension.Bytes, refusal.Dimension);
    }

    [Fact]
    public void AZeroRecordSnapshotIsAdmitted()
    {
        Assert.True(new SingleBlockLimit(1_000).TryAdmit(recordsSoFar: 0, bytesSoFar: 0, nextRecordBytes: 0, out _));
    }

    [Fact]
    public void AConfiguredValueBelowOneIsNotALimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SingleBlockLimit(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SingleBlockLimit(-1));
    }

    [Fact]
    public void TheEffectiveDescriptionNamesBothBounds()
    {
        Assert.Equal("records=1000 bytes=1048576", new SingleBlockLimit(1_000).DescribeEffective());
        Assert.Equal("records=2000 bytes=1048576", new SingleBlockLimit(5_000).DescribeEffective());
    }
}
