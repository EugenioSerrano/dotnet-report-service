using ReportService.Data;

namespace ReportService.Data.Tests;

/// <summary>
/// The diagnostic of the record that would take the block past its limit: the limit,
/// the dimension, the value actually applied and the record's position, in a form that
/// reads the same in every culture and never carries a value of the document.
/// </summary>
public sealed class BlockLimitRefusalTests
{
    [Fact]
    public void TheRecordCountFormIsTheReasonTheSnapshotEndsWith()
    {
        var refusal = new BlockLimitRefusal(BlockLimitDimension.Records, 1_000, 1_001, 4_096);

        Assert.Equal("limit=block dimension=records applied=1000 ordinal=1001 offset=4096", refusal.Diagnostic());
        Assert.Equal("dimension=records applied=1000", refusal.Reason);
    }

    [Fact]
    public void TheByteFormNamesTheByteBound()
    {
        var refusal = new BlockLimitRefusal(BlockLimitDimension.Bytes, 1_048_576, 573, 0);

        Assert.Equal("limit=block dimension=bytes applied=1048576 ordinal=573 offset=0", refusal.Diagnostic());
        Assert.Equal("dimension=bytes applied=1048576", refusal.Reason);
    }

    [Fact]
    public void AnOffsetTheRecordModelDoesNotCarryIsNotInvented()
    {
        var refusal = new BlockLimitRefusal(BlockLimitDimension.Records, 1_000, 1_001, null);

        Assert.Equal("limit=block dimension=records applied=1000 ordinal=1001", refusal.Diagnostic());
    }

    [Fact]
    public void TheTextIsStableAcrossCallsAndCarriesOnlyItsOwnFields()
    {
        var refusal = new BlockLimitRefusal(BlockLimitDimension.Records, 1_000, 1_001, 4_096);

        Assert.Equal(refusal.Diagnostic(), refusal.Diagnostic());

        // The type holds no value, path, key or URL: the diagnostic is the four fields
        // the limit owns, in a fixed shape.
        Assert.Matches(
            "^limit=block dimension=(records|bytes) applied=[0-9]+ ordinal=[0-9]+( offset=[0-9]+)?$",
            refusal.Diagnostic());
    }

    [Fact]
    public void TheAppliedValueIsNeverTheConfigurationTheOperatorWrote()
    {
        // A configuration of five thousand is applied as the hard maximum: the refusal is
        // built from the effective limit, so the operator's number cannot reach it.
        var limit = new SingleBlockLimit(5_000);
        Assert.False(limit.TryAdmit(recordsSoFar: 2_000, bytesSoFar: 0, nextRecordBytes: 1, out var refusal));

        Assert.Equal(2_000, refusal.Applied);
        Assert.DoesNotContain("5000", refusal.Diagnostic(), StringComparison.Ordinal);
    }
}
