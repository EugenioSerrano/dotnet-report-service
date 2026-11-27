using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The open group's accumulated amount: the exact-decimal running sum a block records so
/// the block that holds a split group's last record can print its true total. The cases
/// derive their expectations from the rule — the sum of the group's own records, opening
/// at zero for a group that begins here and at the value a resumed pass carries in —
/// never from the accumulator's implementation.
/// </summary>
public sealed class GroupAmountAccumulatorTests
{
    [Fact]
    public void AGroupThatBeginsHereAccumulatesFromZero()
    {
        var accumulator = new GroupAmountAccumulator();
        accumulator.Begin("a");

        accumulator.Add(1.10m);
        accumulator.Add(2.20m);

        Assert.Equal(3.30m, accumulator.Accumulated);
        Assert.Equal(0m, accumulator.Opening);
        Assert.Equal("a", accumulator.Key);
        Assert.True(accumulator.IsOpen);
    }

    [Fact]
    public void AResumedGroupOpensAtTheValueCarriedIn()
    {
        var accumulator = new GroupAmountAccumulator(6m, "a");

        accumulator.Add(4.40m);

        Assert.Equal(10.40m, accumulator.Accumulated);
        Assert.Equal(6m, accumulator.Opening);
        Assert.Equal("a", accumulator.Key);
    }

    [Fact]
    public void BeginingAnotherGroupClosesTheFirst()
    {
        var accumulator = new GroupAmountAccumulator(6m, "a");
        accumulator.Add(4m);

        accumulator.Begin("b");

        Assert.Equal(0m, accumulator.Accumulated);
        Assert.Equal(0m, accumulator.Opening);
        Assert.Equal("b", accumulator.Key);
    }

    [Fact]
    public void ClosingLeavesNoOpenGroup()
    {
        var accumulator = new GroupAmountAccumulator(6m, "a");

        accumulator.Close();

        Assert.False(accumulator.IsOpen);
        Assert.Null(accumulator.Key);
        Assert.Equal(0m, accumulator.Accumulated);
    }

    [Fact]
    public void TheSumIsExactForNegativeAndLargeValues()
    {
        var accumulator = new GroupAmountAccumulator();
        accumulator.Begin("a");

        accumulator.Add(-0.10m);
        accumulator.Add(0.30m);
        accumulator.Add(9007199254740994m);

        Assert.Equal(9007199254740994.20m, accumulator.Accumulated);
    }
}
