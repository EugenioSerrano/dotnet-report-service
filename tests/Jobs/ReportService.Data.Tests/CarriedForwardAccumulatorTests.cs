using ReportService.Data.Partitioning;

namespace ReportService.Data.Tests;

/// <summary>
/// The per-block opening carried-forward: the exact-decimal running sum of the
/// page-totals amount field over all records in earlier blocks, and the block's own
/// amount total (ADR-022 §3.6 item 2, ADR-021 §3.1 item 5, Block RULE-06). Both
/// depend solely on previous records. This is the BOLT-001↔BOLT-003 boundary: there
/// is no page subtotal, no page-level chain and no grand total here (AC-4).
/// </summary>
public sealed class CarriedForwardAccumulatorTests
{
    [Fact]
    public void TheFirstBlockOpensAtZero()
    {
        var accumulator = new CarriedForwardAccumulator();

        Assert.Equal(0m, accumulator.OpeningCarriedForward);
        Assert.Equal(0m, accumulator.BlockTotal);
    }

    [Fact]
    public void TheBlockTotalIsTheSumOfTheCurrentBlockOnly()
    {
        var accumulator = new CarriedForwardAccumulator();

        accumulator.Add(1.25m);
        accumulator.Add(2.50m);

        Assert.Equal(3.75m, accumulator.BlockTotal);
        Assert.Equal(0m, accumulator.OpeningCarriedForward);
    }

    [Fact]
    public void ClosingABlockFoldsItsTotalIntoTheNextBlockOpening()
    {
        var accumulator = new CarriedForwardAccumulator();

        accumulator.Add(10.00m);
        accumulator.Add(5.00m);
        accumulator.CloseBlock();

        Assert.Equal(15.00m, accumulator.OpeningCarriedForward);
        Assert.Equal(0m, accumulator.BlockTotal);
    }

    [Fact]
    public void TheOpeningIsTheRunningSumOverAllEarlierBlocks()
    {
        var accumulator = new CarriedForwardAccumulator();

        // Block 0: opens at 0, totals 100.
        Assert.Equal(0m, accumulator.OpeningCarriedForward);
        accumulator.Add(40m);
        accumulator.Add(60m);
        accumulator.CloseBlock();

        // Block 1: opens at 100, totals 30.
        Assert.Equal(100m, accumulator.OpeningCarriedForward);
        accumulator.Add(30m);
        accumulator.CloseBlock();

        // Block 2: opens at 130 — the sum of every earlier block's records.
        Assert.Equal(130m, accumulator.OpeningCarriedForward);
    }

    [Fact]
    public void TheSumIsExactDecimalNeverBinaryFloating()
    {
        var accumulator = new CarriedForwardAccumulator();

        accumulator.Add(0.1m);
        accumulator.Add(0.2m);

        // 0.1 + 0.2 is exactly 0.3 in decimal arithmetic; it is 0.30000000000000004 in double.
        Assert.Equal(0.3m, accumulator.BlockTotal);
    }

    [Fact]
    public void NegativeAndZeroAmountsAreCarriedExactly()
    {
        var accumulator = new CarriedForwardAccumulator();

        accumulator.Add(-5.00m);
        accumulator.Add(0m);
        accumulator.Add(5.00m);
        accumulator.CloseBlock();

        Assert.Equal(0.00m, accumulator.OpeningCarriedForward);
    }
}
