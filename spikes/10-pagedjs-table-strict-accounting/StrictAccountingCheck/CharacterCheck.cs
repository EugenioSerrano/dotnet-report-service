using PageAccountingCheck;
using PaginationFixtures;
using TextIntegrityCheck;

namespace StrictAccountingCheck;

/// <summary>The character check's result for one row: pass, or a class with its detail.</summary>
internal sealed record CharacterResult(int Row, string Class, string? Detail)
{
    public bool Pass => Class == "pass";
}

/// <summary>
/// The column-agnostic character check. For each item row of the source fixture
/// it takes every word the printed reading assigns to the row — whatever column
/// the word fell in — and compares the characters with the source:
/// <list type="bullet">
/// <item>four columns: the one word equal to the date and the one word equal to the amount are removed (none: date-missing
/// or amount-missing; more than one: ambiguous), and the rest, normalised, is compared with the description by the text
/// oracle's own comparison (text-dropped, text-repeated, text-altered);</item>
/// <item>five columns (description and note share lines): the multiset of characters of all the row's words against
/// that of date, description, note and amount — it cannot see a reordering inside the row.</item>
/// </list>
/// A row with no word is row-lost. It reads only the PDF and the source fixture, never the text oracle's result.
/// </summary>
internal static class CharacterCheck
{
    public const string Pass = "pass";

    public static List<CharacterResult> Check(PdfReading reading, Fixture fixture)
    {
        var results = new List<CharacterResult>();
        var lines = reading.Document.ContentLines;
        foreach (var item in fixture.Items)
        {
            var seq = item.Sequence!.Value;
            var anchor = reading.SeqWord.GetValueOrDefault(seq);
            var words = new List<PdfWord>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (reading.RowOf[i] != seq)
                {
                    continue;
                }

                foreach (var word in lines[i].Words)
                {
                    if (ReferenceEquals(word, anchor) || word.Text == PdfReading.Continues)
                    {
                        continue;
                    }

                    words.Add(word);
                }
            }

            if (words.Count == 0 && anchor is null)
            {
                results.Add(new CharacterResult(seq, "row-lost", "no word of the row in the printed reading"));
                continue;
            }

            results.Add(fixture.HasNote ? FiveColumn(seq, item, words) : FourColumn(seq, item, words));
        }

        return results;
    }

    private static CharacterResult FourColumn(int seq, FixtureRow item, List<PdfWord> words)
    {
        var date = TextNormalisation.N(item.Date!);
        var amount = TextNormalisation.N(item.Amount!);
        var dates = words.Where(w => TextNormalisation.N(w.Text) == date).ToList();
        var amounts = words.Where(w => TextNormalisation.N(w.Text) == amount).ToList();
        if (dates.Count == 0)
        {
            return new CharacterResult(seq, "date-missing", $"no word equal to the date '{item.Date}'");
        }

        if (amounts.Count == 0)
        {
            return new CharacterResult(seq, "amount-missing", $"no word equal to the amount '{item.Amount}'");
        }

        if (dates.Count > 1 || amounts.Count > 1 || ReferenceEquals(dates[0], amounts[0]))
        {
            return new CharacterResult(seq, "ambiguous", $"{dates.Count} words equal to the date, {amounts.Count} equal to the amount");
        }

        var rest = words.Where(w => !ReferenceEquals(w, dates[0]) && !ReferenceEquals(w, amounts[0]));
        var actual = TextNormalisation.N(string.Join(' ', rest.Select(w => w.Text)));
        var expected = TextNormalisation.N(item.Description!);
        var difference = TextNormalisation.Compare(expected, actual);
        return difference is null
            ? new CharacterResult(seq, Pass, null)
            : new CharacterResult(seq, difference.Class, $"offset {difference.Offset}: expected '{difference.ExpectedContext}', printed '{difference.ActualContext}'");
    }

    private static CharacterResult FiveColumn(int seq, FixtureRow item, List<PdfWord> words)
    {
        var actual = Counts(TextNormalisation.N(string.Join(' ', words.Select(w => w.Text))));
        var expected = Counts(TextNormalisation.N(item.Date!) + TextNormalisation.N(item.Description!) + TextNormalisation.N(item.Note ?? string.Empty) + TextNormalisation.N(item.Amount!));
        var keys = actual.Keys.Union(expected.Keys).ToList();
        var fewer = keys.Any(k => actual.GetValueOrDefault(k) < expected.GetValueOrDefault(k));
        var more = keys.Any(k => actual.GetValueOrDefault(k) > expected.GetValueOrDefault(k));
        var missing = keys.Sum(k => Math.Max(0, expected.GetValueOrDefault(k) - actual.GetValueOrDefault(k)));
        var extra = keys.Sum(k => Math.Max(0, actual.GetValueOrDefault(k) - expected.GetValueOrDefault(k)));
        var detail = $"{missing} characters missing, {extra} extra (character multiset)";
        return (fewer, more) switch
        {
            (false, false) => new CharacterResult(seq, Pass, null),
            (true, false) => new CharacterResult(seq, "text-dropped", detail),
            (false, true) => new CharacterResult(seq, "text-repeated", detail),
            _ => new CharacterResult(seq, "text-altered", detail),
        };
    }

    private static Dictionary<int, int> Counts(string text)
    {
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            var code = char.ConvertToUtf32(text, i);
            counts[code] = counts.GetValueOrDefault(code) + 1;
        }

        return counts;
    }
}

/// <summary>The column geometry of one page: declared, other (with the left edges seen) or no-detail.</summary>
internal sealed record PageGeometry(int Page, string Class, List<double> LeftEdgesMm);

/// <summary>
/// The column-geometry observation: on each page, the left edge of the first word
/// of every item-row line that is neither the row's sequence number, nor a
/// "(continues)" mark, nor in the amount band, against the declared column starts
/// of the text oracle's own geometry plus the stylesheets' 1 mm cell padding.
/// </summary>
internal static class ColumnGeometry
{
    public const double CellPaddingMm = 1.0;
    public const double ToleranceMm = 1.0;

    public static List<PageGeometry> Observe(PdfReading reading, Fixture fixture)
    {
        var starts = DeclaredGeometry.Columns(fixture.Geometry).Select(c => c.StartMm + CellPaddingMm).ToList();
        var edges = Enumerable.Range(1, reading.Pages).ToDictionary(p => p, _ => new List<double>());
        var lines = reading.Document.ContentLines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (reading.RowOf[i] is not { } seq)
            {
                continue;
            }

            var anchor = reading.SeqWord.GetValueOrDefault(seq);
            var first = lines[i].Words.FirstOrDefault(w => !ReferenceEquals(w, anchor) && w.Text != PdfReading.Continues && reading.Rules.ColumnOfWord(w) != "amount");
            if (first is not null && edges.TryGetValue(lines[i].Page, out var list))
            {
                list.Add(Math.Round(first.XMin * 25.4 / 72.0, 1));
            }
        }

        return [.. edges.OrderBy(e => e.Key).Select(e =>
        {
            if (e.Value.Count == 0)
            {
                return new PageGeometry(e.Key, "no-detail", []);
            }

            var declared = e.Value.All(x => starts.Any(s => Math.Abs(x - s) <= ToleranceMm));
            return new PageGeometry(e.Key, declared ? "declared" : "other", [.. e.Value.Distinct().Order()]);
        })];
    }
}
