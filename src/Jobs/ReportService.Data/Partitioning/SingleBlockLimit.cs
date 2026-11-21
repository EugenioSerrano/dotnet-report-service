using System.Globalization;

namespace ReportService.Data;

/// <summary>
/// The single-block limit a PDF job is admitted under. Two numbers bound one block:
/// the configured record count, which an operator may lower, and the block's hard
/// maximum of record bytes. The effective record count is the configured value capped
/// by the hard maximum; the configuration can never raise either bound. This service
/// version prints one block per PDF report: a version that can print more than one
/// block removes this limit, its setting and its test in the same change.
/// </summary>
public sealed class SingleBlockLimit
{
    /// <summary>The most records one block may hold, whatever the configuration says.</summary>
    public const int HardMaxRecords = DataContractLimits.BlockRecords;

    /// <summary>The most bytes of record content one block may hold.</summary>
    public const long HardMaxRecordBytes = DataContractLimits.BlockRecordBytes;

    /// <summary>The limit of a service that configures none: the hard maximum's record count.</summary>
    public static SingleBlockLimit Unconfigured { get; } = new(HardMaxRecords);

    /// <summary>Builds the limit from the configured record count.</summary>
    /// <param name="configuredRecords">The configured record count, at least one.</param>
    public SingleBlockLimit(int configuredRecords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(configuredRecords, 1);
        ConfiguredRecords = configuredRecords;
        EffectiveRecords = Math.Min(configuredRecords, HardMaxRecords);
    }

    /// <summary>The record count the configuration names.</summary>
    public int ConfiguredRecords { get; }

    /// <summary>The record count the pass applies: the configured value, capped by the hard maximum.</summary>
    public int EffectiveRecords { get; }

    /// <summary>Whether the configured value is above the hard maximum and has no effect.</summary>
    public bool IsConfiguredValueIneffective => ConfiguredRecords > HardMaxRecords;

    /// <summary>Answers whether the next record still fits one block.</summary>
    /// <param name="recordsSoFar">How many records the block already holds.</param>
    /// <param name="bytesSoFar">How many bytes of record content the block already holds.</param>
    /// <param name="nextRecordBytes">The wire length of the next record.</param>
    /// <param name="refusal">The refusal when the record does not fit; the default otherwise.</param>
    /// <returns><see langword="true"/> when the record fits.</returns>
    public bool TryAdmit(long recordsSoFar, long bytesSoFar, long nextRecordBytes, out BlockLimitRefusal refusal)
    {
        // The records are evaluated first: a record that crosses both bounds at once is
        // reported by the one the operator configured.
        if (recordsSoFar + 1 > EffectiveRecords)
        {
            refusal = new BlockLimitRefusal(
                BlockLimitDimension.Records,
                EffectiveRecords,
                checked((int)recordsSoFar) + 1,
                null);
            return false;
        }

        if (bytesSoFar + nextRecordBytes > HardMaxRecordBytes)
        {
            refusal = new BlockLimitRefusal(
                BlockLimitDimension.Bytes,
                HardMaxRecordBytes,
                checked((int)recordsSoFar) + 1,
                null);
            return false;
        }

        refusal = default;
        return true;
    }

    /// <summary>The effective record count and the byte bound, as the start-up line names them.</summary>
    public string DescribeEffective() =>
        string.Create(CultureInfo.InvariantCulture, $"records={EffectiveRecords} bytes={HardMaxRecordBytes}");

    /// <summary>The one start-up line: the effective limit, and an ineffective configuration when there is one.</summary>
    public string DescribeStartupLine() => IsConfiguredValueIneffective
        ? "single-block limit effective: " + DescribeEffective()
            + string.Create(CultureInfo.InvariantCulture, $" configured={ConfiguredRecords} is above the hard maximum and has no effect")
        : "single-block limit effective: " + DescribeEffective();
}
