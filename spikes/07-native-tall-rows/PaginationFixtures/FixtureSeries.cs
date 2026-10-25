using System.Globalization;
using System.Text;

namespace PaginationFixtures;

/// <summary>
/// The fixture series. Every fixture is a function of the dataset and its id
/// only: generating twice gives byte-identical files. Grouping follows the
/// earlier layout spike (spikes/02-pdf-layout, BlockSource.cs): a group-start
/// row before the first record of each group, a group-end row with the group's
/// total when the next record of the whole dataset belongs to another group.
/// </summary>
public sealed class FixtureSeries(Dataset dataset)
{
    /// <summary>The record whose field is replaced in the row fixtures.</summary>
    public const int TallRowSequence = 50;

    /// <summary>Characters per content-area height of the earlier spike's calibration (fixed, never re-measured).</summary>
    public const decimal CharactersPerPage = 5257.5m;

    private const string Description = "description";

    /// <summary>The expected lengths of the earlier spike's tall-row anchors, asserted.</summary>
    public static readonly IReadOnlyDictionary<string, int> AnchorLengths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["T150"] = 8_054,
        ["T200"] = 10_560,
        ["T300"] = 15_930,
        ["T500"] = 26_312,
    };

    private static readonly (string Id, decimal K)[] TallRows = [("T150", 1.5m), ("T200", 2m), ("T300", 3m), ("T500", 5m)];
    private static readonly (string Id, int Bytes)[] RecordSizes = [("X64K", 65_536), ("X128K", 131_072), ("X256K", 262_144), ("X512K", 524_288), ("X1M", 1_048_576)];
    private static readonly (string Id, decimal F)[] Sweep =
    [
        ("S080", 0.80m), ("S090", 0.90m), ("S095", 0.95m), ("S098", 0.98m), ("S100", 1.00m),
        ("S102", 1.02m), ("S105", 1.05m), ("S110", 1.10m), ("S125", 1.25m),
    ];

    private readonly IReadOnlyList<DatasetRecord> records = dataset.Records;

    /// <summary>Every fixture id, in generation order.</summary>
    public static IReadOnlyList<string> AllIds { get; } =
    [
        "B1000", "B100", "K1", "T150", "T200", "T300", "T500", "G200",
        "T150u", "T200u", "T300u", "T500u", "X64K", "X128K", "X256K", "X512K", "X1M",
        "T300u-ab", "X64K-ab",
        "F8K-b", "F8K-a", "F26K-b", "F26K-a", "F26K-ba", "F64K-b", "F64K-a", "F256K-b", "F256K-a", "F1M-b", "F1M-a",
        "F26K-1p-b", "F26K-1p-a", "F1M-1p-b", "F1M-1p-a",
        "W2a", "W2b", "W2c", "H1", "H2", "H3",
        "S080", "S090", "S095", "S098", "S100", "S102", "S105", "S110", "S125",
    ];

    /// <summary>Builds the fixture of this id and asserts its texts (A.3); a failed assertion throws.</summary>
    public Fixture Build(string id)
    {
        var fixture = BuildUnchecked(id);
        Assert(fixture);
        return fixture;
    }

    /// <summary>⌈k × 5 257.5⌉, computed in decimal.</summary>
    public static int Target(decimal k) => (int)Math.Ceiling(k * CharactersPerPage);

    private Fixture BuildUnchecked(string id)
    {
        switch (id)
        {
            case "B1000":
                return RowFixture(id, 1000, _ => null);
            case "B100":
                return RowFixture(id, 100, _ => null);
            case "K1":
                return Calibration();
            case "G200":
                return RowFixture(id, 200, r => r.Sequence switch
                {
                    50 => Repeat(r.Description, 12_000),
                    120 => Repeat(r.Description, 30_000),
                    _ => null,
                });
            case "X64K-ab":
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? RecordOfSize(id, r, 65_536) : null) with { AmountAlign = Fixture.AlignBottom };
            case "T300u-ab":
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? UniqueText.OfLength(id, Description, AnchorLengths["T300"]) : null) with { AmountAlign = Fixture.AlignBottom };
            default:
                break;
        }

        foreach (var (anchor, k) in TallRows)
        {
            if (id == anchor)
            {
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? Repeat(r.Description, Target(k)) : null);
            }

            if (id == anchor + "u")
            {
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? UniqueText.OfLength(id, Description, AnchorLengths[anchor]) : null);
            }
        }

        foreach (var (name, bytes) in RecordSizes)
        {
            if (id == name)
            {
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? RecordOfSize(id, r, bytes) : null);
            }
        }

        foreach (var (name, f) in Sweep)
        {
            if (id == name)
            {
                return RowFixture(id, 100, r => r.Sequence == TallRowSequence ? UniqueText.OfLength(id, Description, Target(f)) : null);
            }
        }

        if (id.StartsWith('F'))
        {
            return FreeText(id);
        }

        if (id.StartsWith('W'))
        {
            return TwoTallCells(id);
        }

        if (id.StartsWith('H'))
        {
            return HardBreaks(id);
        }

        throw new ArgumentException($"Unknown fixture id '{id}'.", nameof(id));
    }

    /// <summary>The first <paramref name="count"/> records with record 50's description replaced when <paramref name="replace"/> returns text.</summary>
    private Fixture RowFixture(string id, int count, Func<DatasetRecord, string?> replace)
    {
        var slice = records.Take(count).Select(r => (Record: r, Description: replace(r) ?? r.Description, Note: (string?)null)).ToList();
        return Make(id, slice, NextAfter(count), Fixture.FourColumn, Fixture.WhiteSpaceNormal, [], []);
    }

    /// <summary>The calibration block: the record with the longest description among the first 1 000 (first on a tie), alone.</summary>
    private Fixture Calibration()
    {
        var index = 0;
        for (var i = 1; i < 1000; i++)
        {
            if (records[i].Description.Length > records[index].Description.Length)
            {
                index = i;
            }
        }

        var record = records[index];
        return Make("K1", [(record, record.Description, null)], NextAfter(index + 1), Fixture.FourColumn, Fixture.WhiteSpaceNormal, [], []);
    }

    private Fixture FreeText(string id)
    {
        var parts = id.Split('-');
        var size = parts[0];
        var singleParagraph = parts.Length == 3 && parts[1] == "1p";
        var placement = parts[^1];
        // Byte sizes are measured on the stored paragraphs (the boundary spaces dropped by the split).
        int StoredBytes(string text) => singleParagraph ? FixtureFile.Utf8Bytes(text) : SplitParagraphs(text).Sum(FixtureFile.Utf8Bytes);
        string Text(string field, int divisor) => size switch
        {
            "F8K" => UniqueText.OfLength(id, field, Divide(8_054, divisor)),
            "F26K" => UniqueText.OfLength(id, field, Divide(26_312, divisor)),
            "F64K" => UniqueText.OfSize(id, field, 65_472, 65_536, StoredBytes),
            "F256K" => UniqueText.OfSize(id, field, 262_080, 262_144, StoredBytes),
            "F1M" => UniqueText.OfSize(id, field, 1_048_512, 1_048_576, StoredBytes),
            _ => throw new ArgumentException($"Unknown free-text size in '{id}'.", nameof(id)),
        };

        List<string> Paragraphs(string text) => singleParagraph ? [text] : SplitParagraphs(text);

        IReadOnlyList<string> before = [];
        IReadOnlyList<string> after = [];
        switch (placement)
        {
            case "b":
                before = Paragraphs(Text("freeTextBefore", 1));
                break;
            case "a":
                after = Paragraphs(Text("freeTextAfter", 1));
                break;
            case "ba":
                before = Paragraphs(Text("freeTextBefore", 2));
                after = Paragraphs(Text("freeTextAfter", 2));
                break;
            default:
                throw new ArgumentException($"Unknown placement in '{id}'.", nameof(id));
        }

        var slice = records.Take(20).Select(r => (r, r.Description, (string?)null)).ToList();
        return Make(id, slice, NextAfter(20), Fixture.FourColumn, Fixture.WhiteSpaceNormal, before, after);
    }

    private Fixture TwoTallCells(string id)
    {
        var (description, note) = id switch
        {
            "W2a" => (15_930, 15_930),
            "W2b" => (26_312, 8_054),
            "W2c" => (26_312, 300),
            _ => throw new ArgumentException($"Unknown fixture id '{id}'.", nameof(id)),
        };
        var slice = records.Take(100)
            .Select(r => r.Sequence == TallRowSequence
                ? (r, UniqueText.OfLength(id, Description, description), (string?)UniqueText.OfLength(id, "note", note))
                : (r, r.Description, (string?)string.Empty))
            .ToList();
        return Make(id, slice, NextAfter(100), Fixture.FiveColumn, Fixture.WhiteSpaceNormal, [], []);
    }

    private Fixture HardBreaks(string id)
    {
        string text;
        switch (id)
        {
            case "H1":
                {
                    var tokens = UniqueText.OfLength(id, Description, 15_930).Split(' ');
                    var builder = new StringBuilder();
                    for (var i = 0; i < tokens.Length; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(i % 12 == 0 ? '\n' : ' ');
                        }

                        builder.Append(tokens[i]);
                    }

                    text = builder.ToString();
                    break;
                }

            case "H2":
                text = string.Join('\n', UniqueText.Tokens(id, Description, 3_000));
                break;
            case "H3":
                {
                    var source = UniqueText.OfLength(id, Description, 15_930);
                    var builder = new StringBuilder();
                    var last = 0;
                    while (true)
                    {
                        var space = source.IndexOf(' ', Math.Min(last + 400, source.Length));
                        if (space < 0 || last + 400 >= source.Length)
                        {
                            builder.Append(source, last, source.Length - last);
                            break;
                        }

                        builder.Append(source, last, space - last).Append("\n\n");
                        last = space + 1;
                    }

                    text = builder.ToString();
                    break;
                }

            default:
                throw new ArgumentException($"Unknown fixture id '{id}'.", nameof(id));
        }

        var slice = records.Take(100).Select(r => (r, r.Sequence == TallRowSequence ? text : r.Description, (string?)null)).ToList();
        return Make(id, slice, NextAfter(100), Fixture.FourColumn, Fixture.WhiteSpacePreLine, [], []);
    }

    /// <summary>A unique-token description such that the record, as a compact UTF-8 JSON item, is within [S − 64, S] bytes.</summary>
    private static string RecordOfSize(string id, DatasetRecord record, int bytes) =>
        UniqueText.OfSize(id, Description, bytes - 64, bytes, text => FixtureFile.CompactItemBytes(record, text));

    /// <summary>The record's own description repeated, copies joined by one space, to at least the minimum length.</summary>
    private static string Repeat(string text, int minimumLength)
    {
        var builder = new StringBuilder(text);
        while (builder.Length < minimumLength)
        {
            builder.Append(' ').Append(text);
        }

        return builder.ToString();
    }

    /// <summary>Paragraphs split at the first token boundary after every 2 000 characters (the boundary space dropped).</summary>
    public static List<string> SplitParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var space = start + 2000 < text.Length ? text.IndexOf(' ', start + 2000) : -1;
            if (space < 0)
            {
                paragraphs.Add(text[start..]);
                break;
            }

            paragraphs.Add(text[start..space]);
            start = space + 1;
        }

        return paragraphs;
    }

    private static int Divide(int length, int divisor) => (length + divisor - 1) / divisor;

    private DatasetRecord? NextAfter(int count) => count < records.Count ? records[count] : null;

    private static Fixture Make(
        string id,
        List<(DatasetRecord Record, string Description, string? Note)> slice,
        DatasetRecord? nextAfterBlock,
        string geometry,
        string whiteSpace,
        IReadOnlyList<string> before,
        IReadOnlyList<string> after)
    {
        var rows = new List<FixtureRow>();
        for (var i = 0; i < slice.Count; i++)
        {
            var (record, description, note) = slice[i];
            if (i == 0 || slice[i - 1].Record.GroupId != record.GroupId)
            {
                rows.Add(new FixtureRow { Kind = RowKind.GroupStart, GroupId = record.GroupId, GroupName = record.GroupName });
            }

            rows.Add(new FixtureRow
            {
                Kind = RowKind.Item,
                Sequence = record.Sequence,
                Date = record.Date,
                Description = description,
                Note = note,
                Amount = record.AmountText,
            });

            // A group ends inside the block only when the next record of the whole
            // dataset belongs to another group (or there is none).
            var next = i + 1 < slice.Count ? slice[i + 1].Record : nextAfterBlock;
            if (next is null || next.GroupId != record.GroupId)
            {
                var start = i;
                while (start > 0 && slice[start - 1].Record.GroupId == record.GroupId)
                {
                    start--;
                }

                var total = slice.Skip(start).Take(i - start + 1).Sum(s => s.Record.Amount);
                rows.Add(new FixtureRow { Kind = RowKind.GroupEnd, GroupId = record.GroupId, Total = total.ToString("0.00", CultureInfo.InvariantCulture) });
            }
        }

        var blockTotal = slice.Sum(s => s.Record.Amount).ToString("0.00", CultureInfo.InvariantCulture);
        return new Fixture
        {
            Id = id,
            Title = Captions.TitlePrefix + id,
            Geometry = geometry,
            AmountAlign = Fixture.AlignTop,
            WhiteSpace = whiteSpace,
            FreeTextBefore = before,
            FreeTextAfter = after,
            Rows = rows,
            BlockTotal = blockTotal,
        };
    }

    /// <summary>
    /// The A.3 assertions: no text holds a caption (whitespace removed), the mark
    /// text, a soft hyphen, a control character other than a line break (and a
    /// line break only in hard-break fixtures); unique-token texts hold only the
    /// allowed characters; the earlier spike's anchor lengths hold exactly.
    /// </summary>
    private void Assert(Fixture fixture)
    {
        var forbidden = new[] { Captions.HeaderFourColumn, Captions.HeaderFiveColumn, Captions.BlockTotal, Captions.GroupTotal }
            .Select(RemoveWhitespace)
            .ToArray();
        var lineBreaksAllowed = fixture.WhiteSpace == Fixture.WhiteSpacePreLine;
        var natural = records.Select(r => r.Description).ToHashSet(StringComparer.Ordinal);

        void Check(string where, string text)
        {
            var compact = RemoveWhitespace(text);
            foreach (var caption in forbidden)
            {
                if (compact.Contains(caption, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{fixture.Id} {where}: holds the caption '{caption}'.");
                }
            }

            if (text.Contains(Captions.ContinuesMark, StringComparison.Ordinal) || text.Contains('­', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{fixture.Id} {where}: holds the mark text or a soft hyphen.");
            }

            foreach (var c in text)
            {
                if (char.IsControl(c) && !(c == '\n' && lineBreaksAllowed))
                {
                    throw new InvalidDataException($"{fixture.Id} {where}: holds control character U+{(int)c:X4}.");
                }
            }

            // Texts that are not the dataset's own (nor its repetition) are unique-token texts.
            var isNatural = natural.Contains(text) || where == "title" || where.StartsWith("group", StringComparison.Ordinal);
            var isRepetition = !isNatural && natural.Any(n => n.Length > 0 && text.StartsWith(n + " ", StringComparison.Ordinal) && text.Replace(n, string.Empty, StringComparison.Ordinal).Trim().Length == 0);
            if (!isNatural && !isRepetition && text.Any(c => !UniqueText.IsAllowed(c) && c != '\n'))
            {
                throw new InvalidDataException($"{fixture.Id} {where}: a unique-token text holds a character outside the allowed set.");
            }
        }

        Check("title", fixture.Title);
        for (var i = 0; i < fixture.FreeTextBefore.Count; i++)
        {
            Check($"freeTextBefore[{i}]", fixture.FreeTextBefore[i]);
        }

        for (var i = 0; i < fixture.FreeTextAfter.Count; i++)
        {
            Check($"freeTextAfter[{i}]", fixture.FreeTextAfter[i]);
        }

        foreach (var row in fixture.Rows)
        {
            if (row.Kind == RowKind.Item)
            {
                Check($"row {row.Sequence} description", row.Description!);
                if (!string.IsNullOrEmpty(row.Note))
                {
                    Check($"row {row.Sequence} note", row.Note);
                }
            }
            else if (row.Kind == RowKind.GroupStart)
            {
                Check($"group {row.GroupId}", row.GroupName!);
            }
        }

        var tall = fixture.Items.FirstOrDefault(r => r.Sequence == TallRowSequence);
        if (AnchorLengths.TryGetValue(fixture.Id, out var anchor) || AnchorLengths.TryGetValue(fixture.Id.TrimEnd('u'), out anchor))
        {
            if (tall?.Description?.Length != anchor)
            {
                throw new InvalidDataException($"{fixture.Id}: record 50's description is {tall?.Description?.Length} characters, expected {anchor}.");
            }
        }

        if (fixture.Id == "G200")
        {
            var r120 = fixture.Items.First(r => r.Sequence == 120).Description!.Length;
            if (tall?.Description?.Length != 12_171 || r120 != 30_047)
            {
                throw new InvalidDataException($"G200: lengths {tall?.Description?.Length} / {r120}, expected 12 171 / 30 047.");
            }
        }
    }

    public static string RemoveWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
