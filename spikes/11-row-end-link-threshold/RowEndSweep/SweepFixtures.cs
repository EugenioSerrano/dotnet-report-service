using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaginationFixtures;

namespace RowEndSweep;

/// <summary>
/// The sweep fixtures, written with the fixture library's public API only. Every
/// fixture is the shared row-fixture construction: the first 100 records of the
/// grouped dataset, group rows as they fall, the block-total row, with only record
/// 50's description replaced. Before anything is written, the same builder must
/// reproduce the shared B100, X64K, X128K, T150, T200, T300 and T500 files byte for
/// byte (by their SHA-256 in the shared series' committed sums file).
/// </summary>
internal static class SweepFixtures
{
    public const string SizesFile = "sizes.tsv";
    private const string Description = "description";
    private const int BlockRecords = 100;

    public static int Run(string datasetPath, string sumsPath, string referenceSumsPath, string outDirectory, IReadOnlyList<string>? only)
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
        var records = dataset.Records;
        var tall = records[FixtureSeries.TallRowSequence - 1];
        if (tall.Sequence != FixtureSeries.TallRowSequence)
        {
            Console.Error.WriteLine($"Record at index {FixtureSeries.TallRowSequence - 1} has sequence {tall.Sequence}.");
            return 1;
        }

        // Self-check against the shared series.
        var reference = Sums.Read(referenceSumsPath);
        var checks = new (string Id, string? Text)[]
        {
            ("B100", null),
            ("X64K", UniqText("X64K", tall, 65_536)),
            ("X128K", UniqText("X128K", tall, 131_072)),
            ("T150", Repeat(tall.Description, FixtureSeries.Target(1.5m))),
            ("T200", Repeat(tall.Description, FixtureSeries.Target(2m))),
            ("T300", Repeat(tall.Description, FixtureSeries.Target(3m))),
            ("T500", Repeat(tall.Description, FixtureSeries.Target(5m))),
        };
        foreach (var (id, text) in checks)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(FixtureFile.Serialize(Build(records, id, text))));
            var expected = reference.GetValueOrDefault(id + ".json");
            if (!string.Equals(hash, expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Self-check failed: the builder's {id} hashes to {hash}, the shared series lists {expected ?? "nothing"}.");
                return 1;
            }

            Console.WriteLine($"Self-check: the builder reproduces {id} byte for byte ({hash}).");
        }

        var ids = only ?? SweepPlan.FirstRoundIds();
        Directory.CreateDirectory(outDirectory);
        var sizes = new StringBuilder("id\tcontent\ttargetBytes\trecordBytes\tdescriptionCharacters\tlastWord\n");
        foreach (var id in ids)
        {
            var (content, kib) = SweepPlan.Parse(id);
            var target = SweepPlan.RecordBytes(kib);
            string text;
            try
            {
                text = content == SweepPlan.Uniq ? UniqText(id, tall, target) : RepText(tall, target);
                Assert(id, content, text);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
            {
                Console.Error.WriteLine($"{id}: {e.Message}");
                return 1;
            }

            var bytes = FixtureFile.CompactItemBytes(tall, text);
            FixtureFile.Write(outDirectory, Build(records, id, text));
            sizes.Append(CultureInfo.InvariantCulture, $"{id}\t{content}\t{target}\t{bytes}\t{text.Length}\t{text.Split(' ')[^1]}\n");
        }

        Sums.WriteFolder(outDirectory, Path.Combine(outDirectory, FixtureFile.SumsFileName));
        File.WriteAllText(Path.Combine(outDirectory, SizesFile), sizes.ToString());
        Console.WriteLine($"{ids.Count} fixtures, {FixtureFile.SumsFileName} and {SizesFile} written to {outDirectory}.");
        return 0;
    }

    /// <summary>The shared X series' rule: unique-token text seeded by the fixture id, record 50 within [S − 64, S] bytes.</summary>
    private static string UniqText(string id, DatasetRecord record, int bytes) =>
        UniqueText.OfSize(id, Description, bytes - 64, bytes, text => FixtureFile.CompactItemBytes(record, text));

    /// <summary>The record's own description repeated in whole copies joined by one space, to at least the minimum length.</summary>
    private static string Repeat(string text, int minimumLength)
    {
        var builder = new StringBuilder(text);
        while (builder.Length < minimumLength)
        {
            builder.Append(' ').Append(text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Content "rep": the repeated description, cut at the longest length (by the
    /// library's cut rule, never ending in a space) whose record is at most S bytes;
    /// it must be at least S − 64 bytes.
    /// </summary>
    private static string RepText(DatasetRecord record, int bytes)
    {
        var stream = Repeat(record.Description, bytes + 1);
        var low = 1;
        var high = bytes;
        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            if (FixtureFile.CompactItemBytes(record, UniqueText.Cut(stream, middle)) <= bytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var text = UniqueText.Cut(stream, low);
        var size = FixtureFile.CompactItemBytes(record, text);
        return size >= bytes - 64 && size <= bytes
            ? text
            : throw new InvalidOperationException($"record size {size} outside [{bytes - 64}, {bytes}].");
    }

    /// <summary>The shared series' text assertions, re-applied: no caption, no mark text, no soft hyphen, no control character; unique text in its character set.</summary>
    private static void Assert(string id, string content, string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        foreach (var caption in new[] { Captions.HeaderFourColumn, Captions.HeaderFiveColumn, Captions.BlockTotal, Captions.GroupTotal })
        {
            if (compact.Contains(string.Concat(caption.Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{id}: holds the caption '{caption}'.");
            }
        }

        if (text.Contains(Captions.ContinuesMark, StringComparison.Ordinal) || text.Contains('­', StringComparison.Ordinal) || text.Any(char.IsControl))
        {
            throw new InvalidDataException($"{id}: holds the mark text, a soft hyphen or a control character.");
        }

        if (content == SweepPlan.Uniq && !text.All(UniqueText.IsAllowed))
        {
            throw new InvalidDataException($"{id}: a character outside the unique-text set.");
        }
    }

    /// <summary>The first 100 records as one block, record 50's description replaced when <paramref name="tallDescription"/> is given.</summary>
    public static Fixture Build(IReadOnlyList<DatasetRecord> records, string id, string? tallDescription)
    {
        var slice = records.Take(BlockRecords).ToList();
        var nextAfterBlock = BlockRecords < records.Count ? records[BlockRecords] : null;
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
                Description = record.Sequence == FixtureSeries.TallRowSequence && tallDescription is not null ? tallDescription : record.Description,
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
