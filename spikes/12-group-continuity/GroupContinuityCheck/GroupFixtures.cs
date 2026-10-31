using System.Globalization;
using System.Text.Json;
using PaginationFixtures;

namespace GroupContinuityCheck;

/// <summary>
/// The group fixtures: blocks of the grouped dataset that start at a group's first
/// record, in the shared block construction (the construction of the shared B100 and
/// B1000 and of the ordinary blocks of spikes/09-two-pass-page-subtotals) extended
/// with a start offset. A group-start row precedes a group's first row in the block;
/// a group-end row with the group's total follows its last row only when the next
/// record of the whole dataset belongs to another group; a block that stops inside
/// a group prints no total for it. Before anything is written, offset 0 with 100 and
/// 1 000 records must reproduce the shared B100 and B1000 byte for byte; the whole
/// set is built twice and the two SHA256SUMS files must be identical.
/// </summary>
internal static class GroupFixtures
{
    /// <summary>The shared fixtures this experiment also runs (copied unchanged, checked against their list).</summary>
    public static readonly string[] Shared = ["B100", "K1", "F26K-a", "B1000", "G200", "T300u", "X64K", "X1M", "B4000"];

    public static readonly int[] EdgeLines = [.. Enumerable.Range(30, 37)];

    private sealed record Spec(string Id, int Offset, int Count, string WhiteSpace, IReadOnlyDictionary<int, string> Descriptions, string Case);

    public static int Run(string datasetPath, string sumsPath, string referenceDirectory, string tallFixturePath, string outDirectory)
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

        var records = dataset.Records;
        Console.WriteLine($"Dataset {dataset.FileName} sha256 {dataset.Sha256} ({records.Count} records) verified.");
        var referenceSums = Sums.Read(Path.Combine(referenceDirectory, FixtureFile.SumsFileName));
        foreach (var (id, count) in new[] { ("B100", 100), ("B1000", 1000) })
        {
            var built = FixtureFile.Serialize(Build(records, id, 0, count, Fixture.WhiteSpaceNormal, new Dictionary<int, string>()));
            var reference = File.ReadAllBytes(FixtureFile.PathOf(referenceDirectory, id));
            if (!built.AsSpan().SequenceEqual(reference))
            {
                Console.Error.WriteLine($"Self-check failed: the builder's {id} differs from the shared {id} ({built.Length} against {reference.Length} bytes).");
                return 1;
            }

            Console.WriteLine($"Self-check: the builder reproduces {id} byte for byte ({built.Length} bytes).");
        }

        var tall = FixtureFile.ReadVerified(Path.GetDirectoryName(tallFixturePath)!, Path.GetFileNameWithoutExtension(tallFixturePath), referenceSums);
        var tallText = tall.Items.First(r => r.Sequence == 50).Description!;
        var specs = Specs(records, tallText);

        var first = Path.Combine(outDirectory, "fixtures");
        var second = Path.Combine(outDirectory, "fixtures-second-build");
        foreach (var directory in new[] { first, second })
        {
            Directory.CreateDirectory(directory);
            foreach (var id in Shared)
            {
                var source = FixtureFile.PathOf(referenceDirectory, id);
                _ = FixtureFile.ReadVerified(referenceDirectory, id, referenceSums);
                File.Copy(source, FixtureFile.PathOf(directory, id), overwrite: true);
            }

            foreach (var spec in specs)
            {
                FixtureFile.Write(directory, Build(records, spec.Id, spec.Offset, spec.Count, spec.WhiteSpace, spec.Descriptions));
            }

            Sums.WriteFolder(directory, Path.Combine(directory, FixtureFile.SumsFileName));
        }

        var a = File.ReadAllBytes(Path.Combine(first, FixtureFile.SumsFileName));
        var b = File.ReadAllBytes(Path.Combine(second, FixtureFile.SumsFileName));
        if (!a.AsSpan().SequenceEqual(b))
        {
            Console.Error.WriteLine("The two builds differ: the fixtures are not deterministic.");
            return 1;
        }

        Console.WriteLine($"Two builds identical: {FixtureFile.SumsFileName} {Sums.HashFile(Path.Combine(first, FixtureFile.SumsFileName))}.");
        var report = new List<object>();
        foreach (var spec in specs)
        {
            var fixture = FixtureFile.Read(FixtureFile.PathOf(first, spec.Id));
            var groups = SourceGroups.Of(fixture);
            Console.WriteLine($"{spec.Id}: records {spec.Offset}-{spec.Offset + spec.Count - 1}, "
                + string.Join(", ", groups.Select(g => $"{g.Id} {g.Items.Count} {(g.Closed ? "closed" : "open")}")));
            report.Add(new
            {
                fixture = spec.Id,
                @case = spec.Case,
                records = new { first = spec.Offset, last = spec.Offset + spec.Count - 1, count = spec.Count },
                whiteSpace = spec.WhiteSpace,
                groups = groups.Select(g => new { id = g.Id, firstSequence = g.Items[0].Sequence, lastSequence = g.Items[^1].Sequence, items = g.Items.Count, closed = g.Closed }),
                changedDescriptions = spec.Descriptions.Select(kv => new { sequence = kv.Key, characters = kv.Value.Length, lines = kv.Value.Count(c => c == '\n') + 1 }),
            });
        }

        File.WriteAllText(Path.Combine(outDirectory, "group-fixtures.json"), JsonSerializer.Serialize(new
        {
            dataset = new { file = dataset.FileName, sha256 = dataset.Sha256, records = records.Count },
            selfCheck = "B100 and B1000 reproduced byte for byte",
            secondBuild = "identical",
            sumsSha256 = Sums.HashFile(Path.Combine(first, FixtureFile.SumsFileName)),
            fixtures = report,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Directory.Delete(second, recursive: true);
        return 0;
    }

    private static List<Spec> Specs(IReadOnlyList<DatasetRecord> records, string tallText)
    {
        // Every offset is the first record of a group, checked here, with the group sizes the fixtures rely on.
        Require(records, 401, "G0003", 1);
        Require(records, 402, "G0004", 192);
        Require(records, 594, "G0005", 160);
        Require(records, 754, "G0006", 1200);
        Require(records, 3152, "G0012", 2400);
        Require(records, 0, "G0001", 182);
        var edgeSequence = records[401].Sequence;
        var lastOfFirst = records[181].Sequence;
        var firstOfSecond = records[182].Sequence;
        var specs = new List<Spec>();
        foreach (var n in EdgeLines)
        {
            var text = string.Join('\n', Enumerable.Range(1, n).Select(i => "edgeline" + i.ToString("00", CultureInfo.InvariantCulture)));
            specs.Add(new Spec("GE" + n.ToString(CultureInfo.InvariantCulture), 401, 250, Fixture.WhiteSpacePreLine, new Dictionary<int, string> { [edgeSequence] = text }, "group starting near a page end"));
        }

        specs.Add(new Spec("GS1200", 594, 1366, Fixture.WhiteSpaceNormal, new Dictionary<int, string>(), "group spanning several pages"));
        specs.Add(new Spec("GS2400", 3152, 2408, Fixture.WhiteSpaceNormal, new Dictionary<int, string>(), "group spanning many pages"));
        specs.Add(new Spec("GTL300u", 0, 260, Fixture.WhiteSpaceNormal, new Dictionary<int, string> { [lastOfFirst] = tallText }, "over-tall row as a group's last row"));
        specs.Add(new Spec("GTF300u", 0, 260, Fixture.WhiteSpaceNormal, new Dictionary<int, string> { [firstOfSecond] = tallText }, "over-tall row as a group's first row"));
        return specs;
    }

    private static void Require(IReadOnlyList<DatasetRecord> records, int index, string group, int size)
    {
        var starts = index == 0 || records[index - 1].GroupId != records[index].GroupId;
        var actual = records.Skip(index).TakeWhile(r => r.GroupId == records[index].GroupId).Count();
        if (!starts || records[index].GroupId != group || actual != size)
        {
            throw new InvalidDataException($"record {index}: expected the first record of {group} ({size} records), found {records[index].GroupId} ({actual}, first: {starts}).");
        }
    }

    /// <summary>The shared block construction, from <paramref name="offset"/>, with optional description replacements.</summary>
    public static Fixture Build(IReadOnlyList<DatasetRecord> records, string id, int offset, int count, string whiteSpace, IReadOnlyDictionary<int, string> descriptions)
    {
        var slice = records.Skip(offset).Take(count).ToList();
        var nextAfterBlock = offset + count < records.Count ? records[offset + count] : null;
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
                Description = descriptions.TryGetValue(record.Sequence, out var replaced) ? replaced : record.Description,
                Note = null,
                Amount = record.AmountText,
            });

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
            WhiteSpace = whiteSpace,
            FreeTextBefore = [],
            FreeTextAfter = [],
            Rows = rows,
            BlockTotal = slice.Sum(s => s.Amount).ToString("0.00", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>A group of a fixture's source rows: its caption, its items in order, and whether it closes in the block.</summary>
internal sealed record SourceGroup(string Id, string Caption, List<FixtureRow> Items, bool Closed, string? FixtureTotal)
{
    /// <summary>The group's total computed from its items' source amounts (decimal), never from the fixture's own total.</summary>
    public decimal ComputedTotal => Items.Sum(i => decimal.Parse(i.Amount!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
}

internal static class SourceGroups
{
    public static List<SourceGroup> Of(Fixture fixture)
    {
        var groups = new List<SourceGroup>();
        SourceGroup? current = null;
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    current = new SourceGroup(row.GroupId!, row.CaptionText, [], false, null);
                    groups.Add(current);
                    break;
                case RowKind.Item:
                    current!.Items.Add(row);
                    break;
                case RowKind.GroupEnd:
                    var closed = current! with { Closed = true, FixtureTotal = row.Total };
                    groups[^1] = closed;
                    current = closed;
                    break;
            }
        }

        return groups;
    }
}
