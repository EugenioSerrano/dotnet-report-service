using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PaginationFixtures;

namespace TextIntegrityCheck;

/// <summary>One failed check: rule, class, place (row, region, field, page), first divergence and context.</summary>
internal sealed record Failure(
    string Rule,
    string Class,
    int? Row = null,
    string? Region = null,
    string? Field = null,
    int? Page = null,
    int? Offset = null,
    string? Expected = null,
    string? Actual = null,
    string? Detail = null);

/// <summary>Span of an item row over pages, and where its amount printed.</summary>
internal sealed record RowSpan(int Row, int FirstPage, int LastPage, int Pages, int? AmountPage, string? AmountPosition);

/// <summary>Observations O-1 … O-7 (reported, never failing).</summary>
internal sealed class Observations
{
    /// <summary>O-1 and O-2: every item row spanning two or more pages.</summary>
    public List<RowSpan> MultiPageRows { get; set; } = [];

    /// <summary>O-1: the rows spanning three or more pages.</summary>
    public List<int> RowsSpanningThreeOrMorePages { get; set; } = [];

    /// <summary>O-1 and O-2 for the replaced rows (50, and 120 in the giant-row fixture), whatever their span.</summary>
    public List<RowSpan> TallRows { get; set; } = [];

    /// <summary>O-3: header-row lines per page (the first one included).</summary>
    public SortedDictionary<int, int> HeaderRowsPerPage { get; set; } = [];

    /// <summary>O-4: texts equal after N whose NFKC word sequences differ (word boundaries only).</summary>
    public int TextsCompared { get; set; }

    public int TextsWithWordBoundaryDifferences { get; set; }

    public long ExpectedWords { get; set; }

    public long ActualWords { get; set; }

    /// <summary>O-5: ligature code points in the raw extraction.</summary>
    public int RawLigatures { get; set; }

    /// <summary>O-6: page of each group-start row and whether it is the last content line of its page.</summary>
    public List<GroupStartPlacement> GroupStarts { get; set; } = [];

    /// <summary>O-7: header and footer band text per page, left / centre / right thirds.</summary>
    public List<BandText> Bands { get; set; } = [];
}

internal sealed record GroupStartPlacement(string Group, int Page, bool LastLineOfPage);

internal sealed record BandText(int Page, string HeaderLeft, string HeaderCentre, string HeaderRight, string FooterLeft, string FooterCentre, string FooterRight);

/// <summary>The checker's result for one printed PDF.</summary>
internal sealed class IntegrityResult
{
    public required int Pages { get; init; }

    public required List<Failure> Failures { get; init; }

    public required SortedDictionary<string, string> Rules { get; init; }

    public required Observations Observations { get; init; }

    /// <summary>SHA-256 of N(all content-area text in the checker's reading order).</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Content-area word count of each page.</summary>
    public required List<int> PageWordCounts { get; init; }

    public bool Pass => Failures.Count == 0;
}

/// <summary>
/// The text-integrity rule TI-1 … TI-8 against one fixture. Reads only the
/// printed document (from poppler) and the fixture; the marks mode and the
/// footer mode come from the run's record.
/// </summary>
internal sealed partial class IntegrityRules(Fixture fixture, string marksMode, string footerMode)
{
    public const string MarksNone = "none";
    public const string MarksExpected = "expected";
    public const string FooterNone = "none";
    public const string FooterAccounting = "accounting";

    private readonly IReadOnlyList<(string Column, double StartPt, double EndPt)> columns =
        [.. DeclaredGeometry.Columns(fixture.Geometry).Select(c => (c.Column, DeclaredGeometry.MmToPt(c.StartMm), DeclaredGeometry.MmToPt(c.EndMm)))];

    private readonly string captionN = TextNormalisation.N(Captions.Header(fixture.Geometry));

    private sealed class ItemRow(int sequence, int page)
    {
        public int Sequence { get; } = sequence;

        public int AnchorPage { get; } = page;

        public List<(string Column, PdfWord Word)> Words { get; } = [];

        public SortedSet<int> Pages { get; } = [page];
    }

    private sealed class GroupLine(RowKind kind, Line line)
    {
        public RowKind Kind { get; } = kind;

        public int Page { get; } = line.Page;

        public List<PdfWord> Words { get; } = [.. line.Words];

        public Line First { get; } = line;
    }

    public IntegrityResult Analyze(PrintedDocument document)
    {
        var failures = new List<Failure>();
        var observations = new Observations { RawLigatures = document.RawLigatures };
        var lines = document.ContentLines;

        // TI-1 Structure — page count and size, boundaries, footer band.
        if (document.PageSizes.Count != document.Pages)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Detail: $"pdfinfo reports {document.Pages} pages, the text has {document.PageSizes.Count}"));
        }

        for (var i = 0; i < document.PageSizes.Count; i++)
        {
            var (width, height) = document.PageSizes[i];
            if (Math.Abs(width - DeclaredGeometry.PageWidthPt) > DeclaredGeometry.PageSizeTolerancePt || Math.Abs(height - DeclaredGeometry.PageHeightPt) > DeclaredGeometry.PageSizeTolerancePt)
            {
                failures.Add(new Failure("TI-1", "structure-lost", Page: i + 1, Detail: $"page size {width:0.##} x {height:0.##} pt"));
            }
        }

        foreach (var group in document.CrossingWords.GroupBy(w => w.Page))
        {
            failures.Add(new Failure("TI-1", "structure-lost", Page: group.Key, Detail: $"{group.Count()} word(s) cross the content area's boundary, first '{group.First().Text}'"));
        }

        if (footerMode == FooterNone)
        {
            for (var i = 0; i < document.FooterWords.Count; i++)
            {
                if (document.FooterWords[i].Count > 0)
                {
                    failures.Add(new Failure("TI-1", "structure-lost", Page: i + 1, Region: "footer", Detail: $"footer band holds '{Join(document.FooterWords[i])}'"));
                }
            }
        }

        // Regions: lead until the first header-row line, detail until the block-total line, trail after it.
        var headerIndex = lines.FindIndex(l => TextNormalisation.N(l.Text) == captionN);
        if (headerIndex < 0)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Detail: "no header-row line"));
        }

        var lead = headerIndex < 0 ? lines : lines.Take(headerIndex).ToList();
        var items = new List<ItemRow>();
        var groups = new List<GroupLine>();
        Line? blockLine = null;
        var blockIndex = -1;
        if (headerIndex >= 0)
        {
            ItemRow? current = null;
            GroupLine? currentGroup = null;
            for (var i = headerIndex; i < lines.Count; i++)
            {
                var line = lines[i];
                if (TextNormalisation.N(line.Text) == captionN)
                {
                    observations.HeaderRowsPerPage[line.Page] = observations.HeaderRowsPerPage.GetValueOrDefault(line.Page) + 1;
                    continue;
                }

                var seqWords = line.Words.Where(w => ColumnOf(w) == "seq").ToList();
                var seqText = string.Concat(seqWords.Select(w => w.Text));
                var first = seqWords.FirstOrDefault()?.Text;
                if (seqText.Length > 0 && seqText.All(char.IsAsciiDigit) && seqText.Length < 9)
                {
                    current = new ItemRow(int.Parse(seqText, CultureInfo.InvariantCulture), line.Page);
                    items.Add(current);
                    AddWords(current, line, skipSeq: true);
                    currentGroup = null;
                }
                else if (first is not null && GroupIdPattern().IsMatch(first))
                {
                    currentGroup = new GroupLine(RowKind.GroupStart, line);
                    groups.Add(currentGroup);
                    current = null;
                }
                else if (first == "Group")
                {
                    currentGroup = new GroupLine(RowKind.GroupEnd, line);
                    groups.Add(currentGroup);
                    current = null;
                }
                else if (first == "Block")
                {
                    blockLine = line;
                    blockIndex = i;
                    break;
                }
                else if (seqWords.Count == 0 && current is not null)
                {
                    AddWords(current, line, skipSeq: false);
                }
                else if (seqWords.Count == 0 && currentGroup is not null)
                {
                    currentGroup.Words.AddRange(line.Words);
                }
                else
                {
                    failures.Add(new Failure("TI-1", "structure-lost", Page: line.Page, Detail: $"unclassified detail line '{Shorten(line.Text)}'"));
                    if (current is not null)
                    {
                        AddWords(current, line, skipSeq: false);
                    }
                }
            }
        }

        var trail = blockIndex >= 0 ? lines.Skip(blockIndex + 1).ToList() : [];
        if (headerIndex >= 0 && blockLine is null)
        {
            failures.Add(new Failure("TI-1", "structure-lost", Detail: "no block-total line"));
        }

        var blockN = TextNormalisation.N(Captions.BlockTotal);
        foreach (var extra in trail.Where(l => TextNormalisation.N(l.Text).StartsWith(blockN, StringComparison.Ordinal)))
        {
            failures.Add(new Failure("TI-1", "structure-lost", Page: extra.Page, Detail: "a second block-total line"));
        }

        CheckRows(items, failures, observations);
        CheckGroups(groups, blockLine, failures, observations, lines);
        CheckRegion("free-before", lead.SelectMany(l => l.Words).ToList(), string.Join(' ', new[] { fixture.Title }.Concat(fixture.FreeTextBefore)), failures, observations);
        CheckRegion("free-after", trail.SelectMany(l => l.Words).ToList(), string.Join(' ', fixture.FreeTextAfter), failures, observations);
        CheckMarks(items, document, failures);
        CheckHeaders(document, failures, observations);

        var rules = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var r = 1; r <= 8; r++)
        {
            var name = $"TI-{r}";
            rules[name] = failures.Any(f => f.Rule == name) ? "fail" : "pass";
        }

        var allContent = string.Join(' ', lines.Select(l => l.Text));
        var counts = Enumerable.Range(1, document.Pages).Select(p => 0).ToList();
        foreach (var line in lines)
        {
            if (line.Page >= 1 && line.Page <= counts.Count)
            {
                counts[line.Page - 1] += line.Words.Count;
            }
        }

        return new IntegrityResult
        {
            Pages = document.Pages,
            Failures = failures,
            Rules = rules,
            Observations = observations,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TextNormalisation.N(allContent)))),
            PageWordCounts = counts,
        };
    }

    /// <summary>
    /// The item row each content line belongs to (its sequence), by the same
    /// classification the rules use; null for lead, header-row, group, block-total
    /// and trail lines. Used by the extraction-level controls to place mutations.
    /// </summary>
    public int?[] RowOfLines(PrintedDocument document)
    {
        var lines = document.ContentLines;
        var result = new int?[lines.Count];
        var headerIndex = lines.FindIndex(l => TextNormalisation.N(l.Text) == captionN);
        if (headerIndex < 0)
        {
            return result;
        }

        int? current = null;
        for (var i = headerIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            if (IsHeaderRow(line))
            {
                continue;
            }

            var seqWords = line.Words.Where(w => ColumnOf(w) == "seq").ToList();
            var seqText = string.Concat(seqWords.Select(w => w.Text));
            if (seqText.Length > 0 && seqText.All(char.IsAsciiDigit) && seqText.Length < 9)
            {
                current = int.Parse(seqText, CultureInfo.InvariantCulture);
            }
            else if (seqWords.Count > 0)
            {
                if (seqWords[0].Text == "Block")
                {
                    break;
                }

                current = null;
                continue;
            }

            result[i] = current;
        }

        return result;
    }

    public bool IsHeaderRow(Line line) => TextNormalisation.N(line.Text) == captionN;

    /// <summary>The column band a word falls in, by the centre of its box.</summary>
    public string ColumnOfWord(PdfWord word) => ColumnOf(word);

    /// <summary>TI-2 row coverage, TI-3 row text, TI-4 row fields; O-1 and O-2.</summary>
    private void CheckRows(List<ItemRow> items, List<Failure> failures, Observations observations)
    {
        var expected = fixture.Items.ToList();
        var expectedSequences = expected.Select(r => r.Sequence!.Value).ToList();
        var actualSequences = items.Select(i => i.Sequence).ToList();
        var expectedSet = expectedSequences.ToHashSet();
        var actualCounts = actualSequences.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var coverageFailed = false;
        for (var i = 0; i < expectedSequences.Count; i++)
        {
            var sequence = expectedSequences[i];
            if (!actualCounts.ContainsKey(sequence))
            {
                var previous = items.LastOrDefault(it => expectedSequences.IndexOf(it.Sequence) is >= 0 and var index && index < i);
                failures.Add(new Failure("TI-2", "row-lost", Row: sequence, Page: previous?.Pages.Max));
                coverageFailed = true;
            }
        }

        foreach (var (sequence, count) in actualCounts.Where(kv => kv.Value > 1))
        {
            var second = items.Where(it => it.Sequence == sequence).Skip(1).First();
            failures.Add(new Failure("TI-2", "row-duplicated", Row: sequence, Page: second.AnchorPage, Detail: $"{count} anchors"));
            coverageFailed = true;
        }

        foreach (var item in items.Where(it => !expectedSet.Contains(it.Sequence)))
        {
            failures.Add(new Failure("TI-2", "row-order", Row: item.Sequence, Page: item.AnchorPage, Detail: "anchor not in the fixture"));
            coverageFailed = true;
        }

        if (!coverageFailed && !expectedSequences.SequenceEqual(actualSequences))
        {
            var index = Enumerable.Range(0, expectedSequences.Count).First(i => expectedSequences[i] != actualSequences[i]);
            failures.Add(new Failure("TI-2", "row-order", Row: actualSequences[index], Page: items[index].AnchorPage, Detail: $"position {index + 1}: expected {expectedSequences[index]}"));
        }

        var bySequence = items.GroupBy(i => i.Sequence).ToDictionary(g => g.Key, g => g.First());
        foreach (var row in expected)
        {
            if (!bySequence.TryGetValue(row.Sequence!.Value, out var item))
            {
                continue;
            }

            var sequence = row.Sequence.Value;
            if (item.Words.Any(w => TextNormalisation.HasUnmappedGlyph(w.Word.Text)))
            {
                failures.Add(new Failure("TI-3", "unmapped-glyph", Row: sequence, Page: item.Words.First(w => TextNormalisation.HasUnmappedGlyph(w.Word.Text)).Word.Page));
            }

            CompareField("TI-3", sequence, "description", item.Words.Where(w => w.Column == "description").Select(w => w.Word).ToList(), row.Description!, failures, observations);
            if (fixture.HasNote)
            {
                CompareField("TI-3", sequence, "note", item.Words.Where(w => w.Column == "note").Select(w => w.Word).ToList(), row.Note ?? string.Empty, failures, observations);
            }

            CompareField("TI-4", sequence, "date", item.Words.Where(w => w.Column == "date").Select(w => w.Word).ToList(), row.Date!, failures, observations);
            var amountWords = item.Words.Where(w => w.Column == "amount" && w.Word.Text != Captions.ContinuesMark).Select(w => w.Word).ToList();
            CompareField("TI-4", sequence, "amount", amountWords, row.Amount!, failures, observations);

            var last = item.Pages.Max;
            var amountPage = amountWords.Count > 0 ? amountWords[^1].Page : (int?)null;
            var position = amountPage is null ? null : amountPage == last ? "last" : amountPage == item.Pages.Min ? "first" : "other";
            var span = new RowSpan(sequence, item.Pages.Min, last, last - item.Pages.Min + 1, amountPage, position);
            if (span.Pages >= 2)
            {
                observations.MultiPageRows.Add(span);
            }

            if (span.Pages >= 3)
            {
                observations.RowsSpanningThreeOrMorePages.Add(sequence);
            }

            if (sequence == FixtureSeries.TallRowSequence || (fixture.Id == "G200" && sequence == 120))
            {
                observations.TallRows.Add(span);
            }
        }
    }

    /// <summary>TI-5: group-start and group-end lines in order, and the block-total line; O-6.</summary>
    private void CheckGroups(List<GroupLine> groups, Line? blockLine, List<Failure> failures, Observations observations, List<Line> lines)
    {
        var expected = fixture.Rows.Where(r => r.Kind != RowKind.Item).ToList();
        if (expected.Count != groups.Count)
        {
            failures.Add(new Failure("TI-5", "structure-lost", Region: "groups", Detail: $"{groups.Count} group lines, expected {expected.Count}"));
        }

        for (var i = 0; i < Math.Min(expected.Count, groups.Count); i++)
        {
            var region = $"group {expected[i].GroupId} {(expected[i].Kind == RowKind.GroupStart ? "start" : "end")}";
            if (expected[i].Kind != groups[i].Kind)
            {
                failures.Add(new Failure("TI-5", "structure-lost", Region: region, Page: groups[i].Page, Detail: "group line of the other kind"));
                continue;
            }

            var difference = TextNormalisation.Compare(TextNormalisation.N(expected[i].CaptionText), TextNormalisation.N(Join(groups[i].Words)));
            if (difference is not null)
            {
                failures.Add(new Failure("TI-5", difference.Class, Region: region, Page: groups[i].Page, Offset: difference.Offset, Expected: difference.ExpectedContext, Actual: difference.ActualContext));
            }

            if (groups[i].Kind == RowKind.GroupStart)
            {
                var lastOfPage = lines.Last(l => l.Page == groups[i].Page);
                observations.GroupStarts.Add(new GroupStartPlacement(expected[i].GroupId!, groups[i].Page, ReferenceEquals(lastOfPage, groups[i].First)));
            }
        }

        if (blockLine is not null)
        {
            var difference = TextNormalisation.Compare(TextNormalisation.N(fixture.BlockTotalText), TextNormalisation.N(blockLine.Text));
            if (difference is not null)
            {
                failures.Add(new Failure("TI-5", difference.Class, Region: "block-total", Page: blockLine.Page, Offset: difference.Offset, Expected: difference.ExpectedContext, Actual: difference.ActualContext));
            }
        }
    }

    /// <summary>TI-6: the lead region against title + free text before, the trail region against free text after.</summary>
    private static void CheckRegion(string region, List<PdfWord> words, string expected, List<Failure> failures, Observations observations)
    {
        if (words.Any(w => TextNormalisation.HasUnmappedGlyph(w.Text)))
        {
            failures.Add(new Failure("TI-6", "unmapped-glyph", Region: region, Page: words.First(w => TextNormalisation.HasUnmappedGlyph(w.Text)).Page));
        }

        Compare("TI-6", null, region, null, words, expected, failures, observations);
    }

    /// <summary>TI-7: "(continues)" marks — none in marks mode "none"; in mode "expected", one per non-final page of a row.</summary>
    private void CheckMarks(List<ItemRow> items, PrintedDocument document, List<Failure> failures)
    {
        var allMarks = document.ContentLines.SelectMany(l => l.Words)
            .Concat(document.FooterWords.SelectMany(w => w))
            .Concat(document.HeaderWords.SelectMany(w => w))
            .Count(w => w.Text.Contains(Captions.ContinuesMark, StringComparison.Ordinal));
        if (marksMode == MarksNone)
        {
            if (allMarks > 0)
            {
                failures.Add(new Failure("TI-7", "mark-wrong", Detail: $"{allMarks} mark(s) printed, none expected"));
            }

            return;
        }

        var accepted = 0;
        foreach (var item in items)
        {
            var last = item.Pages.Max;
            var onLastPage = item.Words.Count(w => w.Column == "amount" && w.Word.Page == last && w.Word.Text == Captions.ContinuesMark);
            if (onLastPage > 0)
            {
                // A mark on the page where the row ends is wrong; it is attributed to the row.
                accepted += onLastPage;
                failures.Add(new Failure("TI-7", "mark-wrong", Row: item.Sequence, Page: last, Detail: $"{onLastPage} mark(s) on the page where the row ends"));
            }

            foreach (var page in item.Pages.Where(p => p != last))
            {
                var inRow = item.Words.Count(w => w.Column == "amount" && w.Word.Page == page && w.Word.Text == Captions.ContinuesMark);
                var inFooter = page <= document.FooterWords.Count ? document.FooterWords[page - 1].Count(w => w.Text == Captions.ContinuesMark) : 0;
                if (inRow + inFooter == 1)
                {
                    accepted++;
                }
                else
                {
                    failures.Add(new Failure("TI-7", "mark-wrong", Row: item.Sequence, Page: page, Detail: $"{inRow + inFooter} marks on a page where the row continues"));
                }
            }

            var amountPages = item.Words.Where(w => w.Column == "amount" && w.Word.Text != Captions.ContinuesMark).Select(w => w.Word.Page).Distinct().ToList();
            if (item.Pages.Count > 1 && amountPages.Any(p => p != last))
            {
                failures.Add(new Failure("TI-7", "mark-wrong", Row: item.Sequence, Page: amountPages.First(p => p != last), Detail: "the amount is not on the row's last page"));
            }
        }

        if (allMarks > accepted && !failures.Any(f => f.Rule == "TI-7"))
        {
            failures.Add(new Failure("TI-7", "mark-wrong", Detail: $"{allMarks - accepted} mark(s) where no row continues"));
        }
    }

    /// <summary>TI-8: every page's header band holds the title (left half) and "Page i of N" (right half); O-7.</summary>
    private void CheckHeaders(PrintedDocument document, List<Failure> failures, Observations observations)
    {
        var title = TextNormalisation.N(fixture.Title);
        for (var page = 1; page <= document.HeaderWords.Count; page++)
        {
            var width = page <= document.PageSizes.Count ? document.PageSizes[page - 1].Width : DeclaredGeometry.PageWidthPt;
            var words = document.HeaderWords[page - 1];
            var left = TextNormalisation.N(Join(words.Where(w => w.XCentre < width / 2)));
            var right = TextNormalisation.N(Join(words.Where(w => w.XCentre >= width / 2)));
            var counter = TextNormalisation.N(Captions.PageCounter(page, document.Pages));
            if (left != title || right != counter)
            {
                failures.Add(new Failure("TI-8", "header-wrong", Page: page, Expected: $"{title} | {counter}", Actual: $"{left} | {right}"));
            }

            var footer = page <= document.FooterWords.Count ? document.FooterWords[page - 1] : [];
            observations.Bands.Add(new BandText(
                page,
                Join(words.Where(w => w.XCentre < width / 3)),
                Join(words.Where(w => w.XCentre >= width / 3 && w.XCentre < 2 * width / 3)),
                Join(words.Where(w => w.XCentre >= 2 * width / 3)),
                Join(footer.Where(w => w.XCentre < width / 3)),
                Join(footer.Where(w => w.XCentre >= width / 3 && w.XCentre < 2 * width / 3)),
                Join(footer.Where(w => w.XCentre >= 2 * width / 3))));
        }
    }

    private static void CompareField(string rule, int row, string field, List<PdfWord> words, string expected, List<Failure> failures, Observations observations) =>
        Compare(rule, row, null, field, words, expected, failures, observations);

    /// <summary>Compares N(words) with N(expected); on a difference records the class, the page of the first divergence and the context.</summary>
    private static void Compare(string rule, int? row, string? region, string? field, List<PdfWord> words, string expected, List<Failure> failures, Observations observations)
    {
        var actualText = Join(words);
        var expectedN = TextNormalisation.N(expected);
        var actualN = TextNormalisation.N(actualText);
        observations.TextsCompared++;
        var difference = TextNormalisation.Compare(expectedN, actualN);
        if (difference is null)
        {
            var expectedWords = expected.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var actualWords = actualText.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            observations.ExpectedWords += expectedWords.Length;
            observations.ActualWords += actualWords.Length;
            if (!expectedWords.SequenceEqual(actualWords, StringComparer.Ordinal))
            {
                observations.TextsWithWordBoundaryDifferences++;
            }

            return;
        }

        failures.Add(new Failure(rule, difference.Class, row, region, field, PageAt(words, difference.Offset), difference.Offset, difference.ExpectedContext, difference.ActualContext));
    }

    /// <summary>The page of the word that holds the normalised offset (the last word's page past the end).</summary>
    private static int? PageAt(List<PdfWord> words, int offset)
    {
        var position = 0;
        foreach (var word in words)
        {
            position += TextNormalisation.N(word.Text).Length;
            if (position > offset)
            {
                return word.Page;
            }
        }

        return words.Count > 0 ? words[^1].Page : null;
    }

    private void AddWords(ItemRow item, Line line, bool skipSeq)
    {
        item.Pages.Add(line.Page);
        foreach (var word in line.Words)
        {
            var column = ColumnOf(word);
            if (skipSeq && column == "seq")
            {
                continue;
            }

            item.Words.Add((column, word));
        }
    }

    private string ColumnOf(PdfWord word)
    {
        var x = word.XCentre;
        foreach (var (column, _, end) in columns)
        {
            if (x < end)
            {
                return column;
            }
        }

        return columns[^1].Column;
    }

    private static string Join(IEnumerable<PdfWord> words) => string.Join(' ', words.Select(w => w.Text));

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "…";

    [GeneratedRegex(@"^G\d{4}$")]
    private static partial Regex GroupIdPattern();
}
