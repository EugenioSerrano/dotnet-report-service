namespace ReportService.Data.Partitioning;

/// <summary>
/// The exact-decimal running sum of the open group's page-totals amount field. A group
/// that spans a block boundary is printed in more than one block, so the block that
/// holds its last record must know what its earlier records summed to; this
/// accumulator records that value with the block, in the same sequential pass and
/// depending only on previous records. It opens at zero for a group that starts inside
/// the pass and at the value a resumed pass carries in for a group it continues; it is
/// closed when the group ends. It changes no cut, order, limit or block count.
/// </summary>
public sealed class GroupAmountAccumulator
{
    /// <summary>Builds an accumulator that opens no group.</summary>
    public GroupAmountAccumulator()
    {
    }

    /// <summary>Builds an accumulator that opens <paramref name="key"/> at <paramref name="accumulated"/>.</summary>
    /// <param name="key">The group key the pass continues, or <see langword="null"/> when it continues none.</param>
    /// <param name="accumulated">The group's exact-decimal sum in the earlier blocks.</param>
    public GroupAmountAccumulator(decimal accumulated, string? key)
    {
        Key = key;
        Opening = accumulated;
        Accumulated = accumulated;
    }

    /// <summary>The key of the open group, or <see langword="null"/> when no group is open.</summary>
    public string? Key { get; private set; }

    /// <summary>The open group's sum at the current position, including the earlier blocks it spans.</summary>
    public decimal Accumulated { get; private set; }

    /// <summary>The open group's sum at the position the current block opened, when it continues a group.</summary>
    public decimal Opening { get; private set; }

    /// <summary>Whether a group is open.</summary>
    public bool IsOpen => Key is not null;

    /// <summary>Opens <paramref name="key"/> at zero, closing the group that was open.</summary>
    /// <param name="key">The key of the group that begins here, or <see langword="null"/> when the records are not grouped.</param>
    public void Begin(string? key)
    {
        Key = key;
        Opening = 0m;
        Accumulated = 0m;
    }

    /// <summary>Adds one record's amount to the open group.</summary>
    /// <param name="amount">The record's page-totals amount.</param>
    public void Add(decimal amount) => Accumulated += amount;

    /// <summary>Closes the open group, so a later record begins a new one.</summary>
    public void Close()
    {
        Key = null;
        Opening = 0m;
        Accumulated = 0m;
    }
}
