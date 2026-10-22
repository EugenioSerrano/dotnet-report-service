namespace StreamingDataSpike.Parsing;

/// <summary>
/// Checks the records against the header while they are read, holding only
/// counters and the records of the current block: <c>sequence</c> continues
/// 1…N without gaps, the record count and the exact amount total equal the
/// header totals, and groups are contiguous.
/// </summary>
/// <remarks>
/// The contiguity check accepts a group change only to a strictly greater
/// group identifier. That is valid for the pilot datasets, whose group
/// identifiers increase; it is a shortcut of this measurement, not a rule of
/// the envelope.
/// </remarks>
public sealed class EnvelopeValidator
{
    private readonly EnvelopeLimits _limits;
    private readonly decimal _expectedRecords;
    private readonly decimal _expectedAmount;
    private readonly decimal? _expectedGroups;
    private readonly List<EnvelopeRecord> _block = [];
    private long _blockBytes;
    private string? _lastGroup;

    /// <summary>Creates a validator for the given header.</summary>
    public EnvelopeValidator(EnvelopeHeader header, EnvelopeLimits limits)
    {
        _limits = limits;
        if (header.Values.GetValueOrDefault("totals") is not Dictionary<string, object?> totals
            || totals.GetValueOrDefault("recordCount") is not decimal recordCount
            || totals.GetValueOrDefault("amount") is not decimal amount)
        {
            throw EnvelopeException.Invalid("missing-totals", 0, 0);
        }

        _expectedRecords = recordCount;
        _expectedAmount = amount;
        _expectedGroups = totals.GetValueOrDefault("groupCount") as decimal?;
    }

    /// <summary>Records observed.</summary>
    public long Records { get; private set; }

    /// <summary>Exact sum of <c>amount</c>.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Number of contiguous group runs (0 when the records carry no group).</summary>
    public long GroupRuns { get; private set; }

    /// <summary>Blocks closed or open so far.</summary>
    public long Blocks { get; private set; }

    /// <summary>Observes the next record.</summary>
    public void Observe(EnvelopeRecord record)
    {
        var values = record.Values;
        if (values.TryGetValue("sequence", out var sequence)
            && (sequence is not decimal number || number != record.Ordinal))
        {
            throw EnvelopeException.Invalid("sequence-gap", record.Ordinal, record.ByteOffset);
        }

        if (values.GetValueOrDefault("amount") is not decimal amount)
        {
            throw EnvelopeException.Invalid("type-mismatch", record.Ordinal, record.ByteOffset);
        }

        if (values.GetValueOrDefault("groupId") is string groupId)
        {
            if (_lastGroup is null || !string.Equals(groupId, _lastGroup, StringComparison.Ordinal))
            {
                if (_lastGroup is not null && string.CompareOrdinal(groupId, _lastGroup) < 0)
                {
                    throw EnvelopeException.Invalid("group-not-contiguous", record.Ordinal, record.ByteOffset);
                }

                GroupRuns++;
                _lastGroup = groupId;
            }
        }

        AddToBlock(record);
        Records++;
        Amount += amount;
    }

    /// <summary>Checks the totals after the last record.</summary>
    public void Complete(long endOffset)
    {
        CloseBlock();
        if (Records != _expectedRecords
            || Amount != _expectedAmount
            || (_expectedGroups is { } groups && GroupRuns != groups))
        {
            throw EnvelopeException.Invalid("totals-mismatch", 0, endOffset);
        }
    }

    private void AddToBlock(EnvelopeRecord record)
    {
        if (_block.Count > 0
            && (_block.Count >= _limits.BlockRecords || _blockBytes + record.ByteLength > _limits.BlockBytes))
        {
            CloseBlock();
        }

        if (_block.Count == 0)
        {
            Blocks++;
        }

        _block.Add(record);
        _blockBytes += record.ByteLength;
    }

    private void CloseBlock()
    {
        // The block's records are released here; nothing is written.
        _block.Clear();
        _blockBytes = 0;
    }
}
