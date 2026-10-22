using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfLayoutSpike;

/// <summary>One record of the grouped pilot dataset.</summary>
internal sealed record DatasetRecord(int Sequence, string GroupId, string GroupName, string Date, string Description, decimal Amount)
{
    /// <summary>The amount as printed: invariant culture, exactly two decimals.</summary>
    public string AmountText => Amount.ToString("0.00", CultureInfo.InvariantCulture);
}

internal enum BlockRowKind
{
    GroupStart,
    Item,
    GroupEnd,
}

/// <summary>A row of a block as it is laid out: group start, item or group end.</summary>
internal sealed record BlockRow(BlockRowKind Kind, DatasetRecord? Record, string GroupId, string GroupName, decimal GroupTotal);

/// <summary>A contiguous slice of the dataset laid out as one unit.</summary>
internal sealed record Block(string Name, IReadOnlyList<DatasetRecord> Records, IReadOnlyList<BlockRow> Rows)
{
    public decimal Total => Records.Sum(r => r.Amount);
}

/// <summary>Reads the grouped dataset, checks its hash and produces the blocks.</summary>
internal sealed class BlockSource
{
    private BlockSource(string fileName, string sha256, string reportTitle, string customerName, IReadOnlyList<DatasetRecord> records)
    {
        FileName = fileName;
        Sha256 = sha256;
        ReportTitle = reportTitle;
        CustomerName = customerName;
        Records = records;
    }

    public string FileName { get; }

    public string Sha256 { get; }

    public string ReportTitle { get; }

    public string CustomerName { get; }

    public IReadOnlyList<DatasetRecord> Records { get; }

    /// <summary>
    /// Loads the dataset after checking its SHA-256 against the line of the sums file
    /// that names it. A mismatch throws before anything is laid out.
    /// </summary>
    public static BlockSource Load(string datasetPath, string sumsPath)
    {
        var fileName = Path.GetFileName(datasetPath);
        var bytes = File.ReadAllBytes(datasetPath);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var expected = File.ReadAllLines(sumsPath)
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName)
            .Select(parts => parts[0].ToLowerInvariant())
            .FirstOrDefault()
            ?? throw new InvalidDataException($"No line for {fileName} in {sumsPath}.");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SHA-256 of {fileName} is {actual}, expected {expected}.");
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var header = root.GetProperty("header");
        var title = header.GetProperty("reportTitle").GetString() ?? string.Empty;
        var customer = header.GetProperty("customer").GetProperty("name").GetString() ?? string.Empty;
        var records = new List<DatasetRecord>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            records.Add(new DatasetRecord(
                item.GetProperty("sequence").GetInt32(),
                item.GetProperty("groupId").GetString() ?? string.Empty,
                item.GetProperty("groupName").GetString() ?? string.Empty,
                item.GetProperty("date").GetString() ?? string.Empty,
                item.GetProperty("description").GetString() ?? string.Empty,
                item.GetProperty("amount").GetDecimal()));
        }

        return new BlockSource(fileName, actual, title, customer, records);
    }

    /// <summary>The sequence of the tall row in the giant-row and tall-row fixtures.</summary>
    public const int TallRowSequence = 50;

    /// <summary>The first <paramref name="count"/> records, group rows included as they fall.</summary>
    public Block Slice(string name, int count) => BuildBlock(name, Records.Take(count).ToList(), NextAfter(count));

    /// <summary>
    /// The calibration block: one record, the one with the longest description
    /// among the first 1 000 records (the first such record on a tie), unchanged.
    /// </summary>
    public Block Calibration()
    {
        var first = Records.Take(1000).ToList();
        var index = 0;
        for (var i = 1; i < first.Count; i++)
        {
            if (first[i].Description.Length > first[index].Description.Length)
            {
                index = i;
            }
        }

        return BuildBlock("K1", [first[index]], NextAfter(index + 1));
    }

    /// <summary>
    /// A tall-row fixture: the first 100 records, with only record 50's description
    /// replaced by that description repeated (copies joined by one space) until it
    /// has at least <paramref name="minimumLength"/> characters.
    /// </summary>
    public Block TallRow(string name, int minimumLength)
    {
        var records = Records.Take(100)
            .Select(r => r.Sequence == TallRowSequence ? r with { Description = Repeat(r.Description, minimumLength) } : r)
            .ToList();
        return BuildBlock(name, records, NextAfter(100));
    }

    /// <summary>
    /// The giant-row fixture: the first 200 records, with record 50's description
    /// repeated until it has at least 12 000 characters and record 120's until it
    /// has at least 30 000 (taller than one and than two pages).
    /// </summary>
    public Block GiantRows()
    {
        var records = Records.Take(200)
            .Select(r => r.Sequence switch
            {
                50 => r with { Description = Repeat(r.Description, 12_000) },
                120 => r with { Description = Repeat(r.Description, 30_000) },
                _ => r,
            })
            .ToList();
        return BuildBlock("G200", records, NextAfter(200));
    }

    private static string Repeat(string text, int minimumLength)
    {
        var builder = new StringBuilder(text);
        while (builder.Length < minimumLength)
        {
            builder.Append(' ').Append(text);
        }

        return builder.ToString();
    }

    private DatasetRecord? NextAfter(int count) => count < Records.Count ? Records[count] : null;

    private static Block BuildBlock(string name, List<DatasetRecord> records, DatasetRecord? nextAfterBlock)
    {
        var rows = new List<BlockRow>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (i == 0 || records[i - 1].GroupId != record.GroupId)
            {
                rows.Add(new BlockRow(BlockRowKind.GroupStart, null, record.GroupId, record.GroupName, 0m));
            }

            rows.Add(new BlockRow(BlockRowKind.Item, record, record.GroupId, record.GroupName, 0m));

            // A group ends inside the block only when the next record of the whole
            // dataset belongs to another group (or there is none). A block that
            // ends mid-group prints no group end and no group total.
            var next = i + 1 < records.Count ? records[i + 1] : nextAfterBlock;
            if (next is null || next.GroupId != record.GroupId)
            {
                var start = i;
                while (start > 0 && records[start - 1].GroupId == record.GroupId)
                {
                    start--;
                }

                var total = records.Skip(start).Take(i - start + 1).Sum(r => r.Amount);
                rows.Add(new BlockRow(BlockRowKind.GroupEnd, null, record.GroupId, record.GroupName, total));
            }
        }

        return new Block(name, records, rows);
    }
}
