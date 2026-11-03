using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportService.PdfOracle;

/// <summary>One failed rule: which rule, what kind of failure, and where it was seen.</summary>
/// <param name="Rule">The rule that failed.</param>
/// <param name="Class">The kind of failure: lost, duplicated, out of order, dropped, repeated or altered text, or lost structure.</param>
/// <param name="Row">The record number of the row, when the failure belongs to one.</param>
/// <param name="Field">The document member or anchor the failure belongs to, when it belongs to one.</param>
/// <param name="Page">The page the failure was seen on, when it was seen on one.</param>
/// <param name="Detail">What was seen, in counts and positions: never a value of the data or of the printed text.</param>
internal sealed record Failure(string Rule, string Class, int? Row = null, string? Field = null, int? Page = null, string? Detail = null);

/// <summary>The verdict on one printed document.</summary>
internal sealed class Verdict
{
    /// <summary>The file the verdict is about.</summary>
    public required string File { get; init; }

    /// <summary>How many pages the reading covers.</summary>
    public required int Pages { get; init; }

    /// <summary>How many words it holds.</summary>
    public required int Words { get; init; }

    /// <summary>The state of each rule: pass, fail, or not applicable to this package.</summary>
    public required IReadOnlyDictionary<string, string> Rules { get; init; }

    /// <summary>Why a rule does not apply to this package.</summary>
    public required IReadOnlyDictionary<string, string> NotApplicable { get; init; }

    /// <summary>Every failed rule.</summary>
    public required IReadOnlyList<Failure> Failures { get; init; }

    /// <summary>Whether the document passed every rule that applies to it.</summary>
    public bool Passed => Failures.Count == 0;

    /// <summary>The verdict as the JSON document another tool reads.</summary>
    public string ToJson()
    {
        var failures = new JsonArray();
        foreach (var failure in Failures)
        {
            failures.Add(new JsonObject
            {
                ["rule"] = failure.Rule,
                ["class"] = failure.Class,
                ["row"] = failure.Row,
                ["field"] = failure.Field,
                ["page"] = failure.Page,
                ["detail"] = failure.Detail,
            });
        }

        var notApplicable = new JsonObject();
        foreach (var (rule, reason) in NotApplicable)
        {
            notApplicable[rule] = reason;
        }

        var rules = new JsonObject();
        foreach (var (rule, state) in Rules)
        {
            rules[rule] = state;
        }

        var root = new JsonObject
        {
            ["file"] = File,
            ["pages"] = Pages,
            ["words"] = Words,
            ["passed"] = Passed,
            ["rules"] = rules,
            ["notApplicable"] = notApplicable,
            ["failures"] = failures,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// The text-integrity rules the judge applies to a printed document: its structure,
/// every record printed once and in order, the text of each row against the record
/// it came from, the document header and the text after the table, and the marks a
/// page may not print. Every expectation comes from the fixture the document was
/// printed from, never from the composition the document was laid out from.
/// </summary>
internal static class IntegrityRules
{
    /// <summary>The mark a page may not print on the layout this judge reads.</summary>
    public const string ContinuesMark = "(continues)";

    /// <summary>How far a printed page's size may be from the size the package declares, in points.</summary>
    public const double PageSizeTolerancePt = 0.5;

    /// <summary>Judges <paramref name="extraction"/> against <paramref name="fixture"/>.</summary>
    public static Verdict Judge(PdfExtraction extraction, FixtureSource fixture, ExpectationDocument expectation, string file)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(expectation);

        var placed = Placement.Place(extraction, expectation);
        var classified = Classification.Classify(placed, fixture, expectation);
        var failures = new List<Failure>();

        Structure(extraction, placed, classified, expectation, failures);
        HeadRows(classified, extraction, failures);
        Rows(classified, fixture, failures);
        Regions(classified, fixture, expectation, failures);
        Marks(placed, classified, failures);

        var rules = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var number = 1; number <= 8; number++)
        {
            var rule = "TI-" + number.ToString(CultureInfo.InvariantCulture);
            rules[rule] = rule == "TI-5" ? "n/a" : failures.Any(failure => failure.Rule == rule) ? "fail" : "pass";
        }

        return new Verdict
        {
            File = file,
            Pages = extraction.Pages,
            Words = extraction.Words.Count,
            Rules = rules,
            NotApplicable = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TI-5"] = "the packages this judge reads begin no group and print no block total",
            },
            Failures = failures,
        };
    }

    /// <summary>The page's frame, its edges and the two bands no text may print in.</summary>
    private static void Structure(
        PdfExtraction extraction,
        PlacedDocument placed,
        ClassifiedDocument classified,
        ExpectationDocument expectation,
        List<Failure> failures)
    {
        if (extraction.Pages != extraction.ReportedPages)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Detail: $"the text covers {Count(extraction.Pages)} page(s), the file reports {Count(extraction.ReportedPages)}"));
        }

        for (var index = 0; index < extraction.PageSizes.Count; index++)
        {
            var (width, height) = extraction.PageSizes[index];
            if (Math.Abs(width - expectation.WidthPt) > PageSizeTolerancePt || Math.Abs(height - expectation.HeightPt) > PageSizeTolerancePt)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: "the page is not the size the package declares"));
            }
        }

        foreach (var page in placed.Crossing.GroupBy(word => word.Page))
        {
            failures.Add(new Failure("TI-1", "structure-lost", Page: page.Key, Detail: $"{Count(page.Count())} word(s) cross the content area's edge"));
        }

        for (var index = 0; index < placed.HeaderBand.Count; index++)
        {
            if (placed.HeaderBand[index].Count > 0)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: $"the header band holds {Count(placed.HeaderBand[index].Count)} word(s)"));
            }
        }

        for (var index = 0; index < placed.FooterBand.Count; index++)
        {
            if (placed.FooterBand[index].Count > 0)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: index + 1, Detail: $"the footer band holds {Count(placed.FooterBand[index].Count)} word(s)"));
            }
        }

        var wordsByPage = new int[extraction.Pages];
        foreach (var line in placed.Lines)
        {
            wordsByPage[line.Page - 1] += line.Words.Count;
        }

        for (var page = 1; page <= extraction.Pages; page++)
        {
            if (wordsByPage[page - 1] == 0)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: page, Detail: "the page holds no text"));
            }
        }

        foreach (var line in classified.Unclassified)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Page: line.Page, Detail: "a content line belongs to no region"));
        }
    }

    /// <summary>
    /// The column captions print on every page that prints a detail row. A page that
    /// holds only the document header or the text after the table prints no column,
    /// so it needs no caption: repeating a head row is the page-elements work, not
    /// this rule.
    /// </summary>
    private static void HeadRows(ClassifiedDocument classified, PdfExtraction extraction, List<Failure> failures)
    {
        if (classified.HeadRowPages.Count == 0)
        {
            failures.Add(new Failure("TI-8", "structure-lost", Detail: "no column captions line"));
            return;
        }

        var pages = classified.HeadRowPages.ToHashSet();
        var rowPages = classified.Rows.SelectMany(row => row.Pages).ToHashSet();
        for (var page = 1; page <= extraction.Pages; page++)
        {
            if (rowPages.Contains(page) && !pages.Contains(page))
            {
                failures.Add(new Failure("TI-8", "structure-lost", Page: page, Detail: "the page prints no column captions"));
            }
        }
    }

    /// <summary>Every record once, in order, with its own text.</summary>
    private static void Rows(ClassifiedDocument classified, FixtureSource fixture, List<Failure> failures)
    {
        var expected = fixture.Records.Select(record => record.Sequence).ToList();
        var actual = classified.Rows.Select(row => row.Sequence).ToList();
        var expectedSet = expected.ToHashSet();
        var printed = new Dictionary<int, int>();
        foreach (var sequence in actual)
        {
            printed[sequence] = printed.GetValueOrDefault(sequence) + 1;
        }

        var coverage = false;
        foreach (var sequence in expected)
        {
            if (!printed.ContainsKey(sequence))
            {
                failures.Add(new Failure("TI-2", "row-lost", Row: sequence));
                coverage = true;
            }
        }

        foreach (var sequence in actual.Where(sequence => printed[sequence] > 1).Distinct())
        {
            var second = classified.Rows.Where(row => row.Sequence == sequence).Skip(1).First();
            failures.Add(new Failure("TI-2", "row-duplicated", Row: sequence, Page: second.AnchorPage, Detail: $"{Count(printed[sequence])} rows print it"));
            coverage = true;
        }

        foreach (var row in classified.Rows.Where(row => !expectedSet.Contains(row.Sequence)))
        {
            failures.Add(new Failure("TI-2", "row-order", Row: row.Sequence, Page: row.AnchorPage, Detail: "the record is not in the fixture"));
            coverage = true;
        }

        if (!coverage && !expected.SequenceEqual(actual))
        {
            var index = Enumerable.Range(0, Math.Min(expected.Count, actual.Count)).First(position => expected[position] != actual[position]);
            failures.Add(new Failure("TI-2", "row-order", Row: actual[index], Page: classified.Rows[index].AnchorPage, Detail: $"position {Count(index + 1)} of the detail"));
        }

        var bySequence = new Dictionary<int, ItemRow>();
        foreach (var row in classified.Rows)
        {
            bySequence.TryAdd(row.Sequence, row);
        }

        foreach (var record in fixture.Records)
        {
            if (!bySequence.TryGetValue(record.Sequence, out var row))
            {
                continue;
            }

            CheckField("TI-3", record.Sequence, "description", Field(row, "description"), record.Description, failures);
            CheckField("TI-4", record.Sequence, "date", Field(row, "date"), record.Date, failures);
            var amount = Field(row, "amount");
            CheckField("TI-4", record.Sequence, "amount", amount, record.Amount, failures);
            CheckAmountOnItsRowsFirstPage(record.Sequence, row, amount, failures);
        }
    }

    /// <summary>The document header, and the text the package prints after the table.</summary>
    private static void Regions(ClassifiedDocument classified, FixtureSource fixture, ExpectationDocument expectation, List<Failure> failures)
    {
        var lead = Normalisation.N(string.Join(' ', classified.Lead.Select(line => line.Text)));
        var position = 0;
        foreach (var anchor in expectation.HeaderAnchors)
        {
            var value = Normalisation.N(fixture.ValueOf(anchor));
            if (value.Length == 0)
            {
                continue;
            }

            var found = lead.IndexOf(value, position, StringComparison.Ordinal);
            if (found < 0)
            {
                var anywhere = lead.Contains(value, StringComparison.Ordinal);
                failures.Add(new Failure("TI-6", anywhere ? "text-altered" : "text-dropped", Field: anchor));
                continue;
            }

            position = found + value.Length;
        }

        if (expectation.Trail is null)
        {
            return;
        }

        var expected = Normalisation.N(fixture.ValueOf(expectation.Trail));
        var words = classified.Trail.SelectMany(line => line.Words).ToList();
        var printed = Normalisation.N(string.Join(' ', words.Select(word => word.Text)));
        if (expected.Length == 0)
        {
            if (printed.Length > 0)
            {
                failures.Add(new Failure("TI-6", "structure-lost", Field: expectation.Trail, Detail: "text prints after the table that the data does not hold"));
            }

            return;
        }

        var difference = Normalisation.Compare(expected, printed);
        if (difference is not null)
        {
            failures.Add(new Failure("TI-6", difference.Class, Field: expectation.Trail, Page: PageAt(words, difference.Offset)));
        }
    }

    /// <summary>The mark the layout must not print, inside a row or anywhere else.</summary>
    private static void Marks(PlacedDocument placed, ClassifiedDocument classified, List<Failure> failures)
    {
        foreach (var row in classified.Rows)
        {
            foreach (var (column, word) in row.Words.Where(entry => entry.Word.Text.Contains(ContinuesMark, StringComparison.Ordinal)))
            {
                failures.Add(new Failure("TI-7", "text-altered", Row: row.Sequence, Field: column, Page: word.Page, Detail: "a continuation mark prints"));
            }
        }

        var outside = placed.HeaderBand.Concat(placed.FooterBand)
            .SelectMany(band => band)
            .Concat(classified.Lead.SelectMany(line => line.Words))
            .Concat(classified.Trail.SelectMany(line => line.Words))
            .Concat(classified.Unclassified.SelectMany(line => line.Words))
            .Where(word => word.Text.Contains(ContinuesMark, StringComparison.Ordinal));
        foreach (var page in outside.GroupBy(word => word.Page))
        {
            failures.Add(new Failure("TI-7", "structure-lost", Page: page.Key, Detail: "a continuation mark prints outside every row"));
        }
    }

    private static void CheckField(string rule, int sequence, string field, IReadOnlyList<PdfWord> words, string expected, List<Failure> failures)
    {
        if (words.Any(word => Normalisation.HasUnmappedGlyph(word.Text)))
        {
            failures.Add(new Failure(rule, "text-altered", Row: sequence, Field: field, Page: words.First(word => Normalisation.HasUnmappedGlyph(word.Text)).Page, Detail: "a glyph could not be mapped"));
        }

        var difference = Normalisation.Compare(Normalisation.N(expected), Normalisation.N(string.Join(' ', words.Select(word => word.Text))));
        if (difference is not null)
        {
            failures.Add(new Failure(rule, difference.Class, Row: sequence, Field: field, Page: PageAt(words, difference.Offset)));
        }
    }

    /// <summary>The words of a row that printed in a column, the continuation mark excluded.</summary>
    private static IReadOnlyList<PdfWord> Field(ItemRow row, string column) =>
        [.. row.Words.Where(entry => entry.Column == column && !entry.Word.Text.Contains(ContinuesMark, StringComparison.Ordinal)).Select(entry => entry.Word)];

    /// <summary>
    /// The amount of a row belongs on the page the row begins on: a row that runs over
    /// a page break keeps its amount at its start, never on a page it continues on.
    /// The first page is taken from the row's own first text — the page its number
    /// printed on — so the declaration the document is read against names no page.
    /// </summary>
    private static void CheckAmountOnItsRowsFirstPage(int sequence, ItemRow row, IReadOnlyList<PdfWord> words, List<Failure> failures)
    {
        var elsewhere = words.FirstOrDefault(word => word.Page != row.AnchorPage);
        if (elsewhere is not null)
        {
            failures.Add(new Failure(
                "TI-4",
                "structure-lost",
                Row: sequence,
                Field: "amount",
                Page: elsewhere.Page,
                Detail: $"the amount prints on page {Count(elsewhere.Page)}; the row begins on page {Count(row.AnchorPage)}"));
        }
    }

    /// <summary>The page of the word holding the normalised offset.</summary>
    private static int? PageAt(IReadOnlyList<PdfWord> words, int offset)
    {
        var position = 0;
        foreach (var word in words)
        {
            position += Normalisation.N(word.Text).Length;
            if (position > offset)
            {
                return word.Page;
            }
        }

        return words.Count > 0 ? words[^1].Page : null;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
