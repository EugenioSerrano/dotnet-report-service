using System.Globalization;
using PaginationFixtures;
using PdfLayoutSpike;
using TextIntegrityCheck;
using LayoutRecord = PdfLayoutSpike.DatasetRecord;

namespace PageAccountingCheck;

/// <summary>What a content line is, by the reused checker's own classification.</summary>
internal enum LineKind
{
    Lead,
    Header,
    Item,
    Group,
    Block,
    Trail,
}

/// <summary>
/// A printed PDF as this checker reads it: poppler's words placed in bands and
/// lines by the reused PrintedDocument, the item row of every content line by the
/// reused IntegrityRules.RowOfLines, and from them the word-based facts the
/// accounting rules need — on which pages each row has words, its anchor (the
/// sequence number), its last description word, the pages without detail text.
/// Nothing here comes from the second pass.
/// </summary>
internal sealed class PdfReading
{
    public const string Continues = "(continues)";

    /// <summary>Horizontal tolerance of the footer band check, in points.</summary>
    public const double FooterTolerancePt = 0.5;

    private PdfReading(Fixture fixture, PrintedDocument document, IntegrityRules rules)
    {
        Fixture = fixture;
        Document = document;
        Rules = rules;
        RowOf = rules.RowOfLines(document);
        Kinds = new LineKind[document.ContentLines.Count];
        Classify();
        for (var i = 0; i < document.ContentLines.Count; i++)
        {
            var line = document.ContentLines[i];
            if (RowOf[i] is not { } seq)
            {
                continue;
            }

            if (!RowPages.TryGetValue(seq, out var pages))
            {
                pages = [];
                RowPages[seq] = pages;
                AnchorPage[seq] = line.Page;
                AnchorLine[seq] = i;
                RowOrder.Add(seq);
                var seqWord = line.Words.FirstOrDefault(w => rules.ColumnOfWord(w) == "seq");
                if (seqWord is not null)
                {
                    SeqWord[seq] = seqWord;
                }
            }

            pages.Add(line.Page);
            LastLine[seq] = i;
            foreach (var word in line.Words)
            {
                var column = rules.ColumnOfWord(word);
                if (column == "description")
                {
                    LastDescriptionWord[seq] = word;
                }
                else if (column == "date" && !DateWord.ContainsKey(seq))
                {
                    DateWord[seq] = word;
                }
            }
        }

        var pageCount = document.Pages;
        HeaderOnly = new bool[pageCount];
        WithoutDetail = new bool[pageCount];
        for (var page = 1; page <= pageCount; page++)
        {
            var kinds = Enumerable.Range(0, Kinds.Length).Where(i => document.ContentLines[i].Page == page).Select(i => Kinds[i]).ToList();
            HeaderOnly[page - 1] = kinds.All(k => k == LineKind.Header);
            WithoutDetail[page - 1] = !kinds.Any(k => k is LineKind.Item or LineKind.Group or LineKind.Block);
        }
    }

    public Fixture Fixture { get; }

    public PrintedDocument Document { get; }

    public IntegrityRules Rules { get; }

    public int?[] RowOf { get; }

    public LineKind[] Kinds { get; }

    /// <summary>Pages on which each item row has at least one word (word-based).</summary>
    public Dictionary<int, SortedSet<int>> RowPages { get; } = [];

    public Dictionary<int, int> AnchorPage { get; } = [];

    public Dictionary<int, int> AnchorLine { get; } = [];

    public Dictionary<int, int> LastLine { get; } = [];

    public Dictionary<int, PdfWord> SeqWord { get; } = [];

    public Dictionary<int, PdfWord> LastDescriptionWord { get; } = [];

    public Dictionary<int, PdfWord> DateWord { get; } = [];

    /// <summary>Item rows in the order of their anchor lines.</summary>
    public List<int> RowOrder { get; } = [];

    /// <summary>Per page (index = page − 1): content holding nothing, or only repeated column-header rows.</summary>
    public bool[] HeaderOnly { get; }

    /// <summary>Per page: no item, group-start, group-end or block-total line.</summary>
    public bool[] WithoutDetail { get; }

    public int Pages => Document.Pages;

    public static async Task<PdfReading> ReadAsync(string pdf, Fixture fixture, string marks, string footer, TimeSpan timeout, CancellationToken cancellation)
    {
        var text = await Poppler.ExtractAsync(pdf, timeout, cancellation);
        return From(PrintedDocument.From(text), fixture, marks, footer);
    }

    public static PdfReading From(PrintedDocument document, Fixture fixture, string marks, string footer) =>
        new(fixture, document, new IntegrityRules(fixture, marks, footer));

    /// <summary>The word the end marker wraps: the row's last description word, or its date when the description has none.</summary>
    public PdfWord? EndWord(int seq) =>
        LastDescriptionWord.TryGetValue(seq, out var word) ? word : DateWord.GetValueOrDefault(seq);

    /// <summary>The words of a row on one page, in reading order.</summary>
    public IEnumerable<PdfWord> RowWords(int seq, int page)
    {
        for (var i = 0; i < Document.ContentLines.Count; i++)
        {
            if (RowOf[i] == seq && Document.ContentLines[i].Page == page)
            {
                foreach (var word in Document.ContentLines[i].Words)
                {
                    yield return word;
                }
            }
        }
    }

    /// <summary>The amount-band words of a row (without "(continues)"), with their line index, in reading order.</summary>
    public List<(int Line, PdfWord Word)> AmountWords(int seq)
    {
        var result = new List<(int, PdfWord)>();
        for (var i = 0; i < Document.ContentLines.Count; i++)
        {
            if (RowOf[i] != seq)
            {
                continue;
            }

            foreach (var word in Document.ContentLines[i].Words)
            {
                if (Rules.ColumnOfWord(word) == "amount" && word.Text != Continues)
                {
                    result.Add((i, word));
                }
            }
        }

        return result;
    }

    /// <summary>"(continues)" words of a page: in the content (any row's amount band) and in the footer band.</summary>
    public (int Content, int Footer) MarksOn(int page)
    {
        var content = Document.ContentLines.Where(l => l.Page == page).SelectMany(l => l.Words).Count(w => w.Text == Continues);
        var footer = page <= Document.FooterWords.Count ? Document.FooterWords[page - 1].Count(w => w.Text == Continues) : 0;
        var header = page <= Document.HeaderWords.Count ? Document.HeaderWords[page - 1].Count(w => w.Text == Continues) : 0;
        return (content + header, footer);
    }

    /// <summary>The footer band texts of a page, "(continues)" excluded, by thirds of the page width; null for an empty third.</summary>
    public (string? Left, string? Centre, string? Right) FooterThirds(int page)
    {
        var words = page <= Document.FooterWords.Count ? Document.FooterWords[page - 1].Where(w => w.Text != Continues).ToList() : new List<PdfWord>();
        var width = page <= Document.PageSizes.Count ? Document.PageSizes[page - 1].Width : DeclaredGeometry.PageWidthPt;
        string? Join(IEnumerable<PdfWord> selected)
        {
            var text = string.Join(' ', selected.Select(w => w.Text));
            return text.Length == 0 ? null : text;
        }

        return (Join(words.Where(w => w.XCentre < width / 3)),
                Join(words.Where(w => w.XCentre >= width / 3 && w.XCentre < 2 * width / 3)),
                Join(words.Where(w => w.XCentre >= 2 * width / 3)));
    }

    /// <summary>
    /// The accounting rules' input for this PDF, word-based: one fragment per item
    /// row with at least one word on the page (a page on which a row prints nothing
    /// holds no fragment of it).
    /// </summary>
    public LayoutResult ToLayout()
    {
        var bottom = PrintedDocument.BottomPt;
        var lineOfWord = new Dictionary<PdfWord, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < Document.ContentLines.Count; i++)
        {
            foreach (var word in Document.ContentLines[i].Words)
            {
                lineOfWord[word] = i;
            }
        }

        var layout = new LayoutResult { Total = Document.Pages, FooterOn = true };
        var allFooters = true;
        for (var page = 1; page <= Document.Pages; page++)
        {
            var result = new LayoutPage { Page = page };
            var rowsOnPage = new List<int>();
            for (var i = 0; i < Document.ContentLines.Count; i++)
            {
                if (Document.ContentLines[i].Page == page && RowOf[i] is { } seq && !rowsOnPage.Contains(seq))
                {
                    rowsOnPage.Add(seq);
                }
            }

            foreach (var seq in rowsOnPage)
            {
                var pages = RowPages[seq].ToList();
                var words = RowWords(seq, page).ToList();
                result.Fragments.Add(new LayoutFragment
                {
                    Seq = seq,
                    Fragment = pages.IndexOf(page),
                    Fragments = pages.Count,
                    AmountBefore = string.Join(' ', words.Where(w => Rules.ColumnOfWord(w) == "amount" && w.Text != Continues).Select(w => w.Text)),
                    Mark = words.Any(w => Rules.ColumnOfWord(w) == "amount" && w.Text == Continues),
                    DescChars = words.Where(w => Rules.ColumnOfWord(w) == "description").Sum(w => w.Text.Length),
                });
            }

            // A footer mark belongs to the one row that continues on the page (its fragment is not its last).
            var (content, footer) = MarksOn(page);
            if (footer > 0)
            {
                var continuing = result.Fragments.LastOrDefault(f => f.Fragment < f.Fragments - 1);
                if (continuing is not null)
                {
                    continuing.Mark = true;
                }
            }

            result.Marks = content + footer;
            var (left, centre, right) = FooterThirds(page);
            result.Footer = new FooterTexts { Left = left, Center = centre, Right = right };
            allFooters &= left is not null && centre is not null && right is not null;
            foreach (var word in Document.CrossingWords.Where(w => w.Page == page && w.YMin < bottom && w.YMax > bottom))
            {
                result.BelowArea.Add(lineOfWord.TryGetValue(word, out var line) && RowOf[line] is { } seq ? seq : 0);
            }

            var height = page <= Document.PageSizes.Count ? Document.PageSizes[page - 1].Height : DeclaredGeometry.PageHeightPt;
            var minX = DeclaredGeometry.MmToPt(12) - FooterTolerancePt;
            var maxX = DeclaredGeometry.MmToPt(198) + FooterTolerancePt;
            foreach (var word in page <= Document.FooterWords.Count ? Document.FooterWords[page - 1] : new List<PdfWord>())
            {
                if (word.XMin < minX || word.XMax > maxX || word.YMax > height)
                {
                    result.FooterOverflow.Add(string.Create(CultureInfo.InvariantCulture, $"'{word.Text}' at x {word.XMin:0.##}–{word.XMax:0.##}, y {word.YMin:0.##}–{word.YMax:0.##}"));
                }
            }

            layout.Pages.Add(result);
        }

        layout.MarginBoxesFound = allFooters;
        return layout;
    }

    /// <summary>The block of the earlier spike's accounting rules, from the fixture: records with their group, decimal amounts.</summary>
    public static Block ToBlock(Fixture fixture)
    {
        var records = new List<LayoutRecord>();
        var rows = new List<BlockRow>();
        string groupId = string.Empty;
        string groupName = string.Empty;
        foreach (var row in fixture.Rows)
        {
            switch (row.Kind)
            {
                case RowKind.GroupStart:
                    groupId = row.GroupId ?? string.Empty;
                    groupName = row.GroupName ?? string.Empty;
                    rows.Add(new BlockRow(BlockRowKind.GroupStart, null, groupId, groupName, 0m));
                    break;
                case RowKind.Item:
                    {
                        var record = new LayoutRecord(row.Sequence!.Value, groupId, groupName, row.Date!, row.Description!, Amount(row.Amount!));
                        records.Add(record);
                        rows.Add(new BlockRow(BlockRowKind.Item, record, groupId, groupName, 0m));
                        break;
                    }

                case RowKind.GroupEnd:
                    rows.Add(new BlockRow(BlockRowKind.GroupEnd, null, row.GroupId ?? string.Empty, groupName, Amount(row.Total!)));
                    break;
                default:
                    throw new InvalidDataException($"Unknown row kind {row.Kind}.");
            }
        }

        var block = new Block(fixture.Id, records, rows);
        if (block.Total != Amount(fixture.BlockTotal))
        {
            throw new InvalidDataException($"{fixture.Id}: the amounts sum to {block.Total}, the block total is {fixture.BlockTotal}.");
        }

        return block;
    }

    public static decimal Amount(string text) =>
        decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private void Classify()
    {
        var lines = Document.ContentLines;
        var headerIndex = lines.FindIndex(Rules.IsHeaderRow);
        var trail = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (headerIndex < 0 || i < headerIndex)
            {
                Kinds[i] = LineKind.Lead;
                continue;
            }

            if (trail)
            {
                Kinds[i] = LineKind.Trail;
                continue;
            }

            if (Rules.IsHeaderRow(lines[i]))
            {
                Kinds[i] = LineKind.Header;
                continue;
            }

            if (RowOf[i] is not null)
            {
                Kinds[i] = LineKind.Item;
                continue;
            }

            var seqWords = lines[i].Words.Where(w => Rules.ColumnOfWord(w) == "seq").ToList();
            if (seqWords.Count > 0 && seqWords[0].Text == "Block")
            {
                Kinds[i] = LineKind.Block;
                trail = true;
                continue;
            }

            Kinds[i] = LineKind.Group;
        }
    }
}
