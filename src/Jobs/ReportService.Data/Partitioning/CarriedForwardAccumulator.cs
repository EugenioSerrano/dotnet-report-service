namespace ReportService.Data.Partitioning;

/// <summary>
/// The per-block opening carried-forward: the exact-decimal running sum of the
/// page-totals amount field over all records in earlier blocks, and the block's own
/// amount total. Both depend
/// solely on previous records. This is the partition-stage boundary: there is no page subtotal,
/// no page-level brought/carried-forward chain and no printed grand total here — those
/// rest on the real post-layout distribution and are delivered later.
/// </summary>
public sealed class CarriedForwardAccumulator
{
    /// <summary>The running sum of the amounts of every record in earlier blocks; the current block opens here.</summary>
    public decimal OpeningCarriedForward { get; private set; }

    /// <summary>The sum of the amounts of the current block's records only.</summary>
    public decimal BlockTotal { get; private set; }

    /// <summary>Builds an accumulator that opens at zero.</summary>
    public CarriedForwardAccumulator()
    {
    }

    /// <summary>Builds an accumulator that opens at <paramref name="openingCarriedForward"/>, for a resumed pass.</summary>
    /// <param name="openingCarriedForward">The running sum of the amounts of every already-committed record.</param>
    public CarriedForwardAccumulator(decimal openingCarriedForward) => OpeningCarriedForward = openingCarriedForward;

    /// <summary>Adds one record's amount to the current block's total.</summary>
    /// <param name="amount">The record's page-totals amount.</param>
    public void Add(decimal amount) => BlockTotal += amount;

    /// <summary>Closes the current block, folding its total into the opening carried-forward of the next.</summary>
    public void CloseBlock()
    {
        OpeningCarriedForward += BlockTotal;
        BlockTotal = 0m;
    }
}
