using System.Globalization;
using PaginationFixtures;

namespace PageAccountingCheck;

/// <summary>
/// The supplementary ordinary blocks B250, B500, B2000 and B4000: the first N
/// records of the grouped dataset with its group rows as they fall, natural text,
/// the captions and the block-total row — the construction of the shared series'
/// B100 and B1000, written here with the fixture library's public API only. Before
/// anything is written, the same builder with N = 100 and N = 1000 must reproduce
/// the shared B100 and B1000 files byte for byte.
/// </summary>
internal static class OrdinaryFixtures
{
    public const string StatusFile = "ordinary-fixtures.txt";

    public static readonly IReadOnlyList<(string Id, int Count)> Blocks = [("B250", 250), ("B500", 500), ("B2000", 2000), ("B4000", 4000)];

    public static int Run(string datasetPath, string sumsPath, string referenceDirectory, string outDirectory)
    {
        Dataset dataset;
        try
        {
            dataset = Dataset.Load(datasetPath, sumsPath);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        Console.WriteLine($"Dataset {dataset.FileName} sha256 {dataset.Sha256} ({dataset.Records.Count} records) verified.");
        foreach (var (id, count) in new[] { ("B100", 100), ("B1000", 1000) })
        {
            var built = FixtureFile.Serialize(Build(dataset.Records, id, count));
            var reference = File.ReadAllBytes(FixtureFile.PathOf(referenceDirectory, id));
            if (!built.AsSpan().SequenceEqual(reference))
            {
                Console.Error.WriteLine($"Self-check failed: the builder's {id} differs from the shared {id} ({built.Length} against {reference.Length} bytes).");
                return 1;
            }

            Console.WriteLine($"Self-check: the builder reproduces {id} byte for byte ({built.Length} bytes).");
        }

        Directory.CreateDirectory(outDirectory);
        foreach (var file in Directory.EnumerateFiles(referenceDirectory, "*.json"))
        {
            File.Copy(file, Path.Combine(outDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var (id, count) in Blocks)
        {
            var fixture = Build(dataset.Records, id, count);
            FixtureFile.Write(outDirectory, fixture);
            Console.WriteLine($"{id}: {fixture.Items.Count()} items, {fixture.Rows.Count(r => r.Kind == RowKind.GroupStart)} groups, block total {fixture.BlockTotal}.");
        }

        Sums.WriteFolder(outDirectory, Path.Combine(outDirectory, FixtureFile.SumsFileName));
        var shared = File.ReadAllLines(Path.Combine(referenceDirectory, FixtureFile.SumsFileName));
        var combined = File.ReadAllLines(Path.Combine(outDirectory, FixtureFile.SumsFileName)).ToHashSet(StringComparer.Ordinal);
        var missing = shared.Where(l => !combined.Contains(l)).ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine($"{missing.Count} shared SHA256SUMS line(s) differ in the combined folder, first: {missing[0]}");
            return 1;
        }

        Console.WriteLine($"{shared.Length} shared lines equal; {combined.Count} fixtures and {FixtureFile.SumsFileName} in {outDirectory}.");

        // The status the verdict reads (not a fixture file: SHA256SUMS lists *.json only).
        File.WriteAllText(Path.Combine(outDirectory, StatusFile), $"self-check: pass (B100, B1000 byte-identical)\nshared lines equal: {shared.Length}\nfixtures: {combined.Count}\n");
        return 0;
    }

    /// <summary>The first <paramref name="count"/> records as one block (the shared series' row-fixture construction).</summary>
    public static Fixture Build(IReadOnlyList<DatasetRecord> records, string id, int count)
    {
        var slice = records.Take(count).ToList();
        var nextAfterBlock = count < records.Count ? records[count] : null;
        var rows = new List<FixtureRow>();
        for (var i = 0; i < slice.Count; i++)
        {
            var record = slice[i];
            if (i == 0 || slice[i - 1].GroupId != record.GroupId)
            {
                rows.Add(new FixtureRow { Kind = RowKind.GroupStart, GroupId = record.GroupId, GroupName = record.GroupName });
            }

            rows.Add(new FixtureRow
            {
                Kind = RowKind.Item,
                Sequence = record.Sequence,
                Date = record.Date,
                Description = record.Description,
                Note = null,
                Amount = record.AmountText,
            });

            // A group ends inside the block only when the next record of the whole dataset belongs to another group (or there is none).
            var next = i + 1 < slice.Count ? slice[i + 1] : nextAfterBlock;
            if (next is null || next.GroupId != record.GroupId)
            {
                var start = i;
                while (start > 0 && slice[start - 1].GroupId == record.GroupId)
                {
                    start--;
                }

                var total = slice.Skip(start).Take(i - start + 1).Sum(s => s.Amount);
                rows.Add(new FixtureRow { Kind = RowKind.GroupEnd, GroupId = record.GroupId, Total = total.ToString("0.00", CultureInfo.InvariantCulture) });
            }
        }

        return new Fixture
        {
            Id = id,
            Title = Captions.TitlePrefix + id,
            Geometry = Fixture.FourColumn,
            AmountAlign = Fixture.AlignTop,
            WhiteSpace = Fixture.WhiteSpaceNormal,
            FreeTextBefore = [],
            FreeTextAfter = [],
            Rows = rows,
            BlockTotal = slice.Sum(s => s.Amount).ToString("0.00", CultureInfo.InvariantCulture),
        };
    }
}
