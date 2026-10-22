using System.Globalization;
using System.Text;

namespace ReportService.Samples.DatasetGenerator;

/// <summary>One generated record. The same instance is reused for every record.</summary>
internal sealed class DatasetRecord
{
    /// <summary>Position of the record, starting at 1.</summary>
    public long Sequence { get; set; }

    /// <summary>Group identifier (grouped and volume datasets).</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Group display name (grouped dataset).</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Record date, <c>yyyy-MM-dd</c> (grouped dataset).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Free-text description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Whole quantity (simple dataset).</summary>
    public int WholeQuantity { get; set; }

    /// <summary>Unit price with 2 decimals (simple dataset).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Quantity with exactly 16 significant digits (volume dataset).</summary>
    public decimal PreciseQuantity { get; set; }

    /// <summary>Line amount with 2 decimals.</summary>
    public decimal Amount { get; set; }

    /// <summary>20-digit account identifier with leading zeros (volume dataset).</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Integer reference above 2^53 (volume dataset).</summary>
    public long Reference { get; set; }
}

/// <summary>
/// Produces the deterministic record sequence of one dataset for a seed and a
/// record count. Two instances created with the same arguments produce exactly
/// the same records; nothing is kept in memory between records.
/// </summary>
internal sealed class RecordSource
{
    private const long ReferenceBase = 9_007_199_254_740_993;
    private const int VocabularySize = 500;

    private readonly SplitMix64 random;
    private readonly string[] vocabulary = [];
    private readonly StringBuilder text = new();
    private long produced;
    private int groupIndex = -1;
    private long groupRemaining;
    private string groupName = string.Empty;
    private int formulaCursor;

    /// <summary>Creates the source and draws the header values.</summary>
    public RecordSource(DatasetDefinition definition, ulong seed, long count)
    {
        Definition = definition;
        Seed = seed;
        Count = count;
        random = new SplitMix64(unchecked((seed * 31) + definition.StreamKey));
        CustomerId = "C" + random.NextInt(1, 999_999).ToString("D6", CultureInfo.InvariantCulture);
        CustomerName = Pick(DatasetDefinitions.CustomerNames);
        if (definition.Kind == DatasetKind.Volume)
        {
            vocabulary = new string[VocabularySize];
            for (var i = 0; i < vocabulary.Length; i++)
            {
                vocabulary[i] = Words(random.NextInt(2, 5));
            }
        }
    }

    /// <summary>The dataset this source produces.</summary>
    public DatasetDefinition Definition { get; }

    /// <summary>The seed the sequence derives from.</summary>
    public ulong Seed { get; }

    /// <summary>Number of records produced in total.</summary>
    public long Count { get; }

    /// <summary>Customer identifier of the header.</summary>
    public string CustomerId { get; }

    /// <summary>Customer name of the header.</summary>
    public string CustomerName { get; }

    /// <summary>Number of groups started so far.</summary>
    public int GroupCount => groupIndex + 1;

    /// <summary>The current record, valid after <see cref="MoveNext"/> returned <c>true</c>.</summary>
    public DatasetRecord Current { get; } = new();

    /// <summary>Produces the next record; returns <c>false</c> when the count is reached.</summary>
    public bool MoveNext()
    {
        if (produced == Count)
        {
            return false;
        }

        produced++;
        Current.Sequence = produced;
        switch (Definition.Kind)
        {
            case DatasetKind.Simple:
                NextSimple();
                break;
            case DatasetKind.Grouped:
                NextGrouped();
                break;
            default:
                NextVolume();
                break;
        }

        return true;
    }

    /// <summary>Builds a decimal from an integer and an explicit scale, so the text keeps every decimal place.</summary>
    public static decimal Scaled(long value, byte scale)
    {
        var negative = value < 0;
        var magnitude = negative ? (ulong)(-value) : (ulong)value;
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, negative, scale);
    }

    private void NextSimple()
    {
        var phrase = Pick(DatasetDefinitions.SimplePhrases);
        Current.Description = Current.Sequence switch
        {
            7 => DatasetDefinitions.Hostile[0],
            19 => DatasetDefinitions.Hostile[1],
            _ => phrase,
        };
        Current.WholeQuantity = random.NextInt(1, 20);
        var unitCents = random.NextInt(50, 500_000);
        Current.UnitPrice = Scaled(unitCents, 2);
        Current.Amount = Scaled((long)Current.WholeQuantity * unitCents, 2);
    }

    private void NextGrouped()
    {
        if (groupRemaining == 0)
        {
            groupIndex++;
            groupRemaining = groupIndex switch
            {
                2 => 1,
                5 => 1_200,
                11 => 2_400,
                _ => random.NextInt(20, 400),
            };
            groupName = Pick(DatasetDefinitions.GroupNames);
        }

        groupRemaining--;
        Current.GroupId = GroupId();
        Current.GroupName = groupName;
        Current.Date = "2026-09-" + random.NextInt(1, 30).ToString("D2", CultureInfo.InvariantCulture);
        if (random.NextInt(0, 99) < 2)
        {
            Current.Description = Pick(DatasetDefinitions.Hostile);
        }
        else
        {
            var lengthClass = random.NextInt(0, 99);
            var targetLength = lengthClass switch
            {
                < 60 => random.NextInt(10, 40),
                < 90 => random.NextInt(41, 200),
                < 99 => random.NextInt(201, 600),
                _ => random.NextInt(1_500, 2_500),
            };
            Current.Description = TextOfLength(targetLength);
        }

        Current.Amount = NextAmount();
    }

    private void NextVolume()
    {
        if (groupRemaining == 0)
        {
            groupIndex++;
            groupRemaining = random.NextInt(1, 5_000);
        }

        groupRemaining--;
        Current.GroupId = GroupId();
        Current.AccountId = NextAccountId();
        var descriptionClass = random.NextInt(0, 99);
        Current.Description = descriptionClass switch
        {
            0 => DatasetDefinitions.FormulaLike[formulaCursor++ % DatasetDefinitions.FormulaLike.Length],
            1 => Pick(DatasetDefinitions.Hostile),
            < 32 => "Ref-" + Current.Sequence.ToString("D7", CultureInfo.InvariantCulture) + " " + Words(random.NextInt(2, 4)),
            _ => Pick(vocabulary),
        };
        Current.PreciseQuantity = Scaled(random.NextLong(1_000_000_000_000_000, 9_999_999_999_999_999), 12);
        Current.Amount = NextAmount();
        Current.Reference = ReferenceBase + Current.Sequence;
    }

    private decimal NextAmount()
    {
        var negative = random.NextInt(0, 99) < 3;
        return negative ? Scaled(-random.NextInt(1, 500_000), 2) : Scaled(random.NextInt(0, 5_000_000), 2);
    }

    private string NextAccountId()
    {
        var leadingZeros = random.NextInt(2, 6);
        Span<char> digits = stackalloc char[20];
        digits[..leadingZeros].Fill('0');
        digits[leadingZeros] = (char)('0' + random.NextInt(1, 9));
        for (var i = leadingZeros + 1; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + random.NextInt(0, 9));
        }

        return new string(digits);
    }

    private string GroupId() =>
        "G" + (groupIndex + 1).ToString("D" + Definition.GroupIdDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private string TextOfLength(int targetLength)
    {
        text.Clear();
        while (text.Length < targetLength)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(Pick(DatasetDefinitions.Words));
        }

        return text.ToString();
    }

    private string Words(int count)
    {
        text.Clear();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                text.Append(' ');
            }

            text.Append(Pick(DatasetDefinitions.Words));
        }

        return text.ToString();
    }

    private string Pick(string[] values) => values[random.NextInt(0, values.Length - 1)];
}
