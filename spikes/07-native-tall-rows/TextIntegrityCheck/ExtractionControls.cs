using PaginationFixtures;

namespace TextIntegrityCheck;

/// <summary>The outcome of one extraction-level control.</summary>
internal sealed class ControlOutcome
{
    public required string Id { get; init; }

    public required string Mutation { get; init; }

    public required string Expected { get; init; }

    /// <summary>"yes" | "no" | "not evaluable".</summary>
    public required string Behaved { get; init; }

    public string? Reason { get; init; }

    public List<string> Observed { get; init; } = [];
}

/// <summary>
/// The extraction-level negative controls E1 … E8: mutations applied in memory
/// to the parsed words of one passing run, placed at page breaks, where engine
/// failures happen. Each must fail (or pass) exactly as stated.
/// </summary>
internal static class ExtractionControls
{
    public static List<ControlOutcome> Run(PrintedDocument baseDocument, Fixture fixture, bool hasMultiPageRow)
    {
        var rules = new IntegrityRules(fixture, IntegrityRules.MarksNone, IntegrityRules.FooterNone);
        var rows = rules.RowOfLines(baseDocument);
        var lines = baseDocument.ContentLines;
        var outcomes = new List<ControlOutcome>();

        // The row the mutations act on, and the page break they sit at.
        int row;
        int breakPage;
        if (hasMultiPageRow)
        {
            row = Enumerable.Range(0, lines.Count)
                .Where(i => rows[i] is not null)
                .GroupBy(i => rows[i]!.Value)
                .First(g => g.Select(i => lines[i].Page).Distinct().Count() >= 2)
                .Key;
            breakPage = Enumerable.Range(0, lines.Count).Where(i => rows[i] == row).Min(i => lines[i].Page);
        }
        else
        {
            // Ordinary block: the break between page 1 and page 2; the row is the first item row on page 2.
            breakPage = 1;
            row = Enumerable.Range(0, lines.Count).Where(i => lines[i].Page == 2 && rows[i] is not null).Select(i => rows[i]!.Value).First();
        }

        var nextPage = breakPage + 1;

        // E1 — delete the first description word of the row on the page after the break.
        outcomes.Add(Evaluate("E1", $"delete the first description word of row {row} on page {nextPage}", "text-dropped", row, nextPage, fixture, IntegrityRules.MarksNone, IntegrityRules.FooterNone, document =>
        {
            var rowsNow = rules.RowOfLines(document);
            var index = Enumerable.Range(0, document.ContentLines.Count).First(i => rowsNow[i] == row && document.ContentLines[i].Page == nextPage);
            var line = document.ContentLines[index];
            var word = line.Words.First(w => rules.ColumnOfWord(w) == "description");
            line.Words.Remove(word);
        }));

        // E2 — copy the last content line of the break page to the top of the next page.
        var lastIndex = Enumerable.Range(0, lines.Count).Last(i => lines[i].Page == breakPage && rows[i] is not null);
        var copiedHoldsAnchor = lines[lastIndex].Words.Any(w => rules.ColumnOfWord(w) == "seq");
        var e2Row = rows[lastIndex]!.Value;
        outcomes.Add(Evaluate("E2", $"copy the last line of page {breakPage} (row {e2Row}) to the top of page {nextPage}", copiedHoldsAnchor ? "row-duplicated" : "text-repeated", e2Row, null, fixture, IntegrityRules.MarksNone, IntegrityRules.FooterNone, document =>
        {
            var source = document.ContentLines[lastIndex];
            var insertAt = document.ContentLines.FindIndex(l => l.Page == nextPage);
            document.ContentLines.Insert(insertAt, new Line { Page = nextPage, Words = [.. source.Words.Select(w => w with { Page = nextPage })] });
        }));

        // E3 — delete every line of the row.
        outcomes.Add(Evaluate("E3", $"delete every line of row {row}", "row-lost", row, null, fixture, IntegrityRules.MarksNone, IntegrityRules.FooterNone, document =>
        {
            var rowsNow = rules.RowOfLines(document);
            var keep = Enumerable.Range(0, document.ContentLines.Count).Where(i => rowsNow[i] != row).Select(i => document.ContentLines[i]).ToList();
            document.ContentLines.Clear();
            document.ContentLines.AddRange(keep);
        }));

        // E4 — swap two adjacent, different description words of the row on the page after the break.
        outcomes.Add(Evaluate("E4", $"swap two adjacent description words of row {row} on page {nextPage}", "text-altered", row, null, fixture, IntegrityRules.MarksNone, IntegrityRules.FooterNone, document =>
        {
            var rowsNow = rules.RowOfLines(document);
            foreach (var line in Enumerable.Range(0, document.ContentLines.Count).Where(i => rowsNow[i] == row && document.ContentLines[i].Page >= nextPage).Select(i => document.ContentLines[i]))
            {
                var words = line.Words.Select((w, i) => (w, i)).Where(x => rules.ColumnOfWord(x.w) == "description").ToList();
                for (var k = 0; k + 1 < words.Count; k++)
                {
                    if (words[k].w.Text != words[k + 1].w.Text)
                    {
                        line.Words[words[k].i] = words[k].w with { Text = words[k + 1].w.Text };
                        line.Words[words[k + 1].i] = words[k + 1].w with { Text = words[k].w.Text };
                        return;
                    }
                }
            }

            throw new InvalidOperationException("No two different adjacent description words to swap.");
        }));

        // E5 … E8 — marks on a row spanning two or more pages (simulating a marking layout:
        // the amount moved to the row's last line, as such a layout prints it).
        if (!hasMultiPageRow)
        {
            foreach (var id in new[] { "E5", "E6", "E7", "E8" })
            {
                outcomes.Add(new ControlOutcome { Id = id, Mutation = "marks on a multi-page row", Expected = "-", Behaved = "not evaluable", Reason = "no passing run has a row spanning two or more pages" });
            }

            return outcomes;
        }

        var pages = Enumerable.Range(0, lines.Count).Where(i => rows[i] == row).Select(i => lines[i].Page).Distinct().Order().ToList();
        var nonFinal = pages.Take(pages.Count - 1).ToList();
        var finalPage = pages[^1];

        void MoveAmount(PrintedDocument document)
        {
            var rowsNow = rules.RowOfLines(document);
            var rowLines = Enumerable.Range(0, document.ContentLines.Count).Where(i => rowsNow[i] == row).Select(i => document.ContentLines[i]).ToList();
            var amount = rowLines.SelectMany(l => l.Words.Where(w => rules.ColumnOfWord(w) == "amount").Select(w => (l, w))).ToList();
            foreach (var (l, w) in amount)
            {
                l.Words.Remove(w);
            }

            var target = rowLines.Last();
            var y = target.Words[0];
            foreach (var (_, w) in amount)
            {
                target.Words.Add(w with { Page = target.Page, YMin = y.YMin, YMax = y.YMax });
            }
        }

        void MarkRow(PrintedDocument document, IEnumerable<int> onPages)
        {
            var rowsNow = rules.RowOfLines(document);
            foreach (var page in onPages)
            {
                var line = Enumerable.Range(0, document.ContentLines.Count).Where(i => rowsNow[i] == row && document.ContentLines[i].Page == page).Select(i => document.ContentLines[i]).Last();
                var y = line.Words[0];
                line.Words.Add(new PdfWord(page, DeclaredGeometry.MmToPt(176), y.YMin, DeclaredGeometry.MmToPt(193), y.YMax, Captions.ContinuesMark));
            }
        }

        void MarkFooter(PrintedDocument document, IEnumerable<int> onPages)
        {
            foreach (var page in onPages)
            {
                var y = DeclaredGeometry.MmToPt(285);
                document.FooterWords[page - 1].Add(new PdfWord(page, DeclaredGeometry.MmToPt(150), y - 4, DeclaredGeometry.MmToPt(170), y + 4, Captions.ContinuesMark));
            }
        }

        outcomes.Add(EvaluatePass("E5", $"amount of row {row} on its last page, a mark in its amount band on pages {string.Join(',', nonFinal)}", fixture, IntegrityRules.FooterNone, d =>
        {
            MoveAmount(d);
            MarkRow(d, nonFinal);
        }));
        outcomes.Add(Evaluate("E6", $"as E5, plus a mark on the row's final page {finalPage}", "mark-wrong", row, null, fixture, IntegrityRules.MarksExpected, IntegrityRules.FooterNone, d =>
        {
            MoveAmount(d);
            MarkRow(d, [.. nonFinal, finalPage]);
        }));
        outcomes.Add(EvaluatePass("E7", $"amount of row {row} on its last page, a mark in the footer band of pages {string.Join(',', nonFinal)}", fixture, IntegrityRules.FooterAccounting, d =>
        {
            MoveAmount(d);
            MarkFooter(d, nonFinal);
        }));
        outcomes.Add(Evaluate("E8", $"as E5, with the mark of page {nonFinal[0]} dropped", "mark-wrong", row, null, fixture, IntegrityRules.MarksExpected, IntegrityRules.FooterNone, d =>
        {
            MoveAmount(d);
            MarkRow(d, nonFinal.Skip(1));
        }));
        return outcomes;

        ControlOutcome EvaluatePass(string id, string mutation, Fixture f, string footerMode, Action<PrintedDocument> change)
        {
            var document = baseDocument.Clone();
            change(document);
            var result = new IntegrityRules(f, IntegrityRules.MarksExpected, footerMode).Analyze(document);
            var ok = result.Rules["TI-7"] == "pass" && result.Rules["TI-4"] == "pass";
            return new ControlOutcome
            {
                Id = id,
                Mutation = mutation,
                Expected = "TI-7 pass (and TI-4 pass)",
                Behaved = ok ? "yes" : "no",
                Observed = [.. result.Failures.Take(5).Select(Describe)],
            };
        }

        ControlOutcome Evaluate(string id, string mutation, string expectedClass, int expectedRow, int? expectedPage, Fixture f, string marksMode, string footerMode, Action<PrintedDocument> change)
        {
            var document = baseDocument.Clone();
            try
            {
                change(document);
            }
            catch (InvalidOperationException e)
            {
                return new ControlOutcome { Id = id, Mutation = mutation, Expected = expectedClass, Behaved = "no", Reason = "mutation could not be applied: " + e.Message };
            }

            var result = new IntegrityRules(f, marksMode, footerMode).Analyze(document);
            var hit = result.Failures.Any(x => x.Class == expectedClass && x.Row == expectedRow && (expectedPage is null || x.Page == expectedPage));
            var expected = $"{expectedClass} at row {expectedRow}" + (expectedPage is null ? string.Empty : $", page {expectedPage}");
            return new ControlOutcome
            {
                Id = id,
                Mutation = mutation,
                Expected = expected,
                Behaved = hit ? "yes" : "no",
                Observed = [.. result.Failures.Take(5).Select(Describe)],
            };
        }
    }

    public static string Describe(Failure f) =>
        $"{f.Rule} {f.Class}" + (f.Row is null ? string.Empty : $" row {f.Row}") + (f.Region is null ? string.Empty : $" region {f.Region}")
        + (f.Field is null ? string.Empty : $" field {f.Field}") + (f.Page is null ? string.Empty : $" page {f.Page}") + (f.Detail is null ? string.Empty : $" ({f.Detail})");
}
