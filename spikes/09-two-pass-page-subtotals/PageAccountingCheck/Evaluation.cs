using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaginationFixtures;
using PdfLayoutSpike;

namespace PageAccountingCheck;

/// <summary>One line of the accounting rules' result: rule number, check name, PASS or FAIL, first failing page, detail.</summary>
internal sealed record RuleLine(int Rule, string Name, string Outcome, int? Page, string? Detail);

/// <summary>One violation found by the full evaluation of rules 1, 4 and 5 over every page, with its cause.</summary>
internal sealed record FullItem(int Rule, int? Page, int? Row, string Cause, bool Placement, string Detail);

/// <summary>Observation O-E: a page without detail text and what the second pass printed on it.</summary>
internal sealed record PageWithoutDetail(int Page, string Kind, string? Brought, string? Subtotal, string? Carried, bool Mark, bool? SubtotalZero, bool? CarriedEqualsBrought, List<int> RowsCovering);

/// <summary>Observation O-A: where a split row's amount printed.</summary>
internal sealed record AmountPlacement(int Row, int FirstPage, int LastPage, int? AmountPage, string? Position, bool? OnLastLine);

/// <summary>The accounting checker's record of one run (accounting.json).</summary>
internal sealed class AccountingEntry
{
    public string Run { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public string Markup { get; set; } = string.Empty;

    public string Strategy { get; set; } = string.Empty;

    public int Repetition { get; set; }

    public string? Mutation { get; set; }

    public string? Fault { get; set; }

    public int Group { get; set; }

    public string Pass1Outcome { get; set; } = string.Empty;

    public string Pass2Outcome { get; set; } = string.Empty;

    /// <summary>checked | no-pdf | no-check.</summary>
    public string Verdict { get; set; } = "no-check";

    public string? Reason { get; set; }

    /// <summary>confirmed | refuted | partial | inconclusive (&lt;class&gt;).</summary>
    public string Outcome { get; set; } = string.Empty;

    public List<string> Refuting { get; set; } = [];

    public List<string> Placement { get; set; } = [];

    public int? Pass1Pages { get; set; }

    public int? Pass2Pages { get; set; }

    public List<int>? Pass1PageWordCounts { get; set; }

    /// <summary>Row 50's first and last page in this checker's reading of the pass-1 PDF (the faults' expected pages).</summary>
    public int? TallRowFirstPage { get; set; }

    public int? TallRowLastPage { get; set; }

    /// <summary>The page of the first row's sequence number in this checker's reading of the pass-1 PDF.</summary>
    public int? FirstRowPage { get; set; }

    public List<RuleLine> Rules { get; set; } = [];

    public List<FullItem> FullEvaluation { get; set; } = [];

    public List<CheckFailure> Unchanged { get; set; } = [];

    public string? BoxVerdict { get; set; }

    public List<string>? Pass1Boxes { get; set; }

    public List<string>? Pass2Boxes { get; set; }

    public RoundedReference? RefRounded { get; set; }

    public long? ChangedPixels { get; set; }

    public int? NewWords { get; set; }

    public bool? SquareOnWhite { get; set; }

    public List<CheckFailure> ReadBack { get; set; } = [];

    public int? ReadBackRowsJudged { get; set; }

    public bool? ReadBackReliable { get; set; }

    public string? Ti1Verdict { get; set; }

    public string? Ti2Verdict { get; set; }

    public List<string> TiFailures { get; set; } = [];

    /// <summary>The accounting checker's own failing checks (rules 1–9, UO-1 … UO-4, RB-a … RB-d), each "check|class|place".</summary>
    public List<string> FailureSet { get; set; } = [];

    public List<PageWithoutDetail> PagesWithoutDetail { get; set; } = [];

    public int HeaderOnlyPages { get; set; }

    public List<AmountPlacement> AmountPlacements { get; set; } = [];

    public int SplitRows { get; set; }

    public int RowsOverThreePages { get; set; }

    public int LargestSpan { get; set; }

    public Dictionary<string, string?> MarkersVersusLayout { get; set; } = [];

    public string? ReadBackMapSha256 { get; set; }

    public string? FooterTextsSha256 { get; set; }

    public string? ContentSha256 { get; set; }

    public List<string> RenderSha256 { get; set; } = [];

    /// <summary>The colour check of the pass-2 PDF (a failure is a stop-and-ask, never an outcome).</summary>
    public List<CheckFailure> ColourFailures { get; set; } = [];

    public long? ColourChangedPixels { get; set; }

    /// <summary>qpdf --check of the pass-2 PDF by the assembly experiment's rule (a fail is a stop-and-ask).</summary>
    public StructureCheck? Structure { get; set; }

    public double CheckSeconds { get; set; }
}

/// <summary>The evaluation of one run (D.3, D.4). Expected values come from the fixture and from this checker's own reading.</summary>
internal static class Evaluation
{
    private static readonly Dictionary<string, int> RuleNumbers = new(StringComparer.Ordinal)
    {
        [AccountingVerifier.Rows] = 1,
        [AccountingVerifier.EndPageSubtotals] = 2,
        [AccountingVerifier.Chain] = 3,
        [AccountingVerifier.VisibleAmounts] = 4,
        [AccountingVerifier.Marks] = 5,
        [AccountingVerifier.SplitAttributes] = 5,
        [AccountingVerifier.Geometry] = 6,
        [AccountingVerifier.FooterNoDisplacement] = 7,
        [AccountingVerifier.Escaping] = 8,
        [AccountingVerifier.ColumnAlignment] = 9,
        [AccountingVerifier.DescriptionText] = 9,
        [AccountingVerifier.SplitRows] = 9,
    };

    private static readonly HashSet<string> ObservationChecks = new(StringComparer.Ordinal)
    {
        AccountingVerifier.ColumnAlignment, AccountingVerifier.DescriptionText, AccountingVerifier.SplitRows,
    };

    public static async Task EvaluateAsync(
        AccountingEntry entry, TwoPassLine run, Fixture fixture, string runsDirectory, IReadOnlyDictionary<string, IntegrityEntry> integrity,
        IReadOnlyDictionary<string, IntegrityEntry>? reference, string workDirectory, TimeSpan timeout, CancellationToken cancellation)
    {
        var stamped = run.Strategy == "stamped";
        var block = PdfReading.ToBlock(fixture);

        // The pass-1 reading: the layout the markers came from.
        var pass1Pdf = run.Pass1.Pdf is null ? null : Path.Combine(runsDirectory, run.Pass1.Pdf);
        if (run.Pass1.Outcome != "ok" || pass1Pdf is null || !File.Exists(pass1Pdf))
        {
            entry.Verdict = "no-pdf";
            entry.Outcome = $"inconclusive (pass 1 {run.Pass1.Outcome})";
            entry.Reason = run.Pass1.Reason;
            return;
        }

        var pass1 = await PdfReading.ReadAsync(pass1Pdf, fixture, "none", "none", timeout, cancellation);
        entry.Pass1Pages = pass1.Pages;
        entry.Pass1PageWordCounts = [.. Enumerable.Range(1, pass1.Pages).Select(p => pass1.Document.ContentLines.Where(l => l.Page == p).Sum(l => l.Words.Count))];
        ObserveMarkersVersusLayout(entry, run, pass1, reference);
        if (pass1.RowPages.TryGetValue(50, out var tall))
        {
            entry.TallRowFirstPage = tall.Min;
            entry.TallRowLastPage = tall.Max;
        }

        if (pass1.RowOrder.Count > 0)
        {
            entry.FirstRowPage = pass1.AnchorPage[pass1.RowOrder[0]];
        }

        // Read-back, judged against the pass-1 reading (also for a guard failure: the reading is written before the guard).
        var readBack = Files.ReadReadBack(runsDirectory, run.Pass2.Readback);
        if (readBack is not null)
        {
            var judged = ReadBackJudge.Judge(readBack, pass1, fixture);
            entry.ReadBack = judged.Failures;
            entry.ReadBackRowsJudged = judged.RowsJudged;
            entry.ReadBackReliable = judged.Reliable && run.Pass2.Outcome != "guard-failed";
            entry.ReadBackMapSha256 = Sha(string.Join(";", readBack.Rows.OrderBy(r => r.Seq).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Seq}:{r.StartPage}-{r.EndPage}"))));
        }

        if (run.Pass2.Outcome == "guard-failed")
        {
            entry.Verdict = "checked";
            entry.Refuting.Add($"readback-guard: {run.Pass2.Reason}");
            entry.FailureSet.Add("readback-guard");
            entry.Outcome = "refuted";
            entry.ReadBackReliable = false;
            return;
        }

        var pass2Pdf = run.Pass2.Pdf is null ? null : Path.Combine(runsDirectory, run.Pass2.Pdf);
        if (run.Pass2.Outcome != "ok" || pass2Pdf is null || !File.Exists(pass2Pdf))
        {
            entry.Verdict = "no-pdf";
            entry.Outcome = $"inconclusive (pass 2 {run.Pass2.Outcome})";
            entry.Reason = run.Pass2.Reason;
            return;
        }

        var pass2 = await PdfReading.ReadAsync(pass2Pdf, fixture, "expected", "accounting", timeout, cancellation);
        entry.Pass2Pages = pass2.Pages;
        entry.Verdict = "checked";

        // Rules 1–6, 8, 9 on the pass-2 PDF; rule 7 between the two PDFs; all unchanged.
        var layout2 = pass2.ToLayout();
        var layout1 = pass1.ToLayout();
        var result = new RunResult
        {
            Id = run.Run,
            Configuration = $"{run.Markup}-{run.Strategy}",
            Block = fixture.Id,
            Footer = true,
            Repetition = run.Repetition,
            Purpose = "two-pass",
            Status = "ok",
            Dialogs = run.Pass1.Dialogs ?? -1,
            AbortedRequests = run.Pass1.AbortedRequests ?? -1,
            ScriptsExpected = 0,
            ScriptsFound = run.Pass1.Scripts,
            Layout = layout2,
        };
        var checks = AccountingVerifier.VerifyPaged(block, result);
        checks.Add(AccountingVerifier.CompareLayouts(AccountingVerifier.FooterNoDisplacement, layout1, layout2));
        entry.Rules = [.. checks.Select(c => new RuleLine(RuleNumbers.GetValueOrDefault(c.Name), c.Name, c.Outcome, c.FirstFailingPage, c.Detail))];
        foreach (var check in checks.Where(c => !c.Pass && !ObservationChecks.Contains(c.Name)))
        {
            entry.FailureSet.Add($"{check.Name}|p{check.FirstFailingPage}");
        }

        // N1 — on a rule 1, 4 or 5 failure, every page is evaluated and each violation gets its cause.
        if (checks.Any(c => !c.Pass && c.Name is AccountingVerifier.Rows or AccountingVerifier.VisibleAmounts or AccountingVerifier.Marks))
        {
            entry.FullEvaluation = FullEvaluation(block, layout2, pass2);
        }

        Classify(entry, checks, stamped);

        // UO-1 … UO-4.
        (int, double, double, double)? square = run.Fault == "AC-7"
            ? (1, DeclaredGeometry.MmToPt(6.0), DeclaredGeometry.MmToPt(148.5), DeclaredGeometry.MmToPt(1.0))
            : null;
        var unchanged = await UnchangedOutside.CheckAsync(pass1Pdf, pass2Pdf, pass1, pass2, stamped, workDirectory, square, timeout, cancellation, colour: true);
        entry.ColourFailures = unchanged.ColourFailures;
        entry.ColourChangedPixels = unchanged.ColourPages.Where(p => p.Changed >= 0).Sum(p => p.Changed);
        entry.Structure = await StampingSideEffects.CheckAsync(pass2Pdf, timeout, cancellation);
        entry.Unchanged = unchanged.Failures;
        entry.BoxVerdict = unchanged.BoxVerdict;
        entry.Pass1Boxes = unchanged.Pass1Boxes;
        entry.Pass2Boxes = unchanged.Pass2Boxes;
        entry.RefRounded = unchanged.Rounded;
        entry.ChangedPixels = unchanged.Pages.Where(p => p.Changed >= 0).Sum(p => p.Changed);
        entry.NewWords = unchanged.NewWords;
        entry.SquareOnWhite = unchanged.SquareOnWhite;
        entry.RenderSha256 = [.. unchanged.Pages.Select(p => p.RenderSha256)];
        foreach (var failure in unchanged.Failures)
        {
            entry.FailureSet.Add($"{failure.Check}|{failure.Class}|p{failure.Page}");
            if (failure.Class == "pass1-box-unexpected")
            {
                entry.Outcome = "inconclusive (pass-1 page box)";
                return;
            }

            entry.Refuting.Add($"{failure.Check} {failure.Class} p{failure.Page}: {failure.Detail}");
        }

        // RB-a … RB-d.
        foreach (var failure in entry.ReadBack)
        {
            entry.FailureSet.Add($"{failure.Check}|{failure.Class}" + (failure.Check is "RB-a" or "RB-b" ? $"|{RowOf(failure.Detail)}" : string.Empty));
            if (failure.Check != "RB-d" || stamped)
            {
                entry.Refuting.Add($"{failure.Check} {failure.Class}: {failure.Detail}");
            }
        }

        // TI-1 … TI-8 of the reused checker, read, never recomputed.
        ClassifyIntegrity(entry, integrity.GetValueOrDefault(run.Run + ".p2"), pass2, "pass 2", required: true);
        if (!stamped)
        {
            ClassifyIntegrity(entry, integrity.GetValueOrDefault(run.Run + ".p1"), pass1, "pass 1", required: true);
        }

        entry.Ti2Verdict = integrity.GetValueOrDefault(run.Run + ".p2")?.Verdict;
        entry.Ti1Verdict = stamped ? null : integrity.GetValueOrDefault(run.Run + ".p1")?.Verdict;
        entry.ContentSha256 = integrity.GetValueOrDefault(run.Run + ".p2")?.ContentSha256;

        Observe(entry, pass2, layout2);
        entry.Outcome = entry.Outcome.StartsWith("inconclusive", StringComparison.Ordinal) ? entry.Outcome
            : entry.Refuting.Count > 0 ? "refuted"
            : entry.Placement.Count > 0 ? "partial"
            : "confirmed";
    }

    /// <summary>Rules 1–9: a failing rule refutes, except rules 1, 4 and 5 when every violation found over all pages is a placement cause.</summary>
    private static void Classify(AccountingEntry entry, List<CheckResult> checks, bool stamped)
    {
        foreach (var check in checks.Where(c => !c.Pass && !ObservationChecks.Contains(c.Name)))
        {
            var rule = RuleNumbers.GetValueOrDefault(check.Name);
            var items = entry.FullEvaluation.Where(i => i.Rule == rule).ToList();
            if (rule is 1 or 4 or 5 && items.Count > 0 && items.All(i => i.Placement))
            {
                foreach (var cause in items.Select(i => i.Cause).Distinct(StringComparer.Ordinal))
                {
                    entry.Placement.Add($"rule {rule} {cause}: " + string.Join(", ", items.Where(i => i.Cause == cause).Select(i => i.Row is null ? $"p{i.Page}" : $"row {i.Row} p{i.Page}")));
                }

                continue;
            }

            entry.Refuting.Add($"rule {rule} {check.Name} p{check.FirstFailingPage}: {check.Detail}");
        }

        _ = stamped;
    }

    /// <summary>
    /// Every violation of rules 1, 4 and 5 over all pages (the rules themselves stop at
    /// the first). Causes: rule 1 "header-only-gap" when every page between two
    /// fragments of a row is header-only; rule 4 "amount-not-on-last-page" when the
    /// row's amount prints exactly once, on a fragment that is not its last; rule 5
    /// "mark-on-header-only-page" for one mark on a header-only page inside a row's
    /// span. Anything else is not a placement cause.
    /// </summary>
    public static List<FullItem> FullEvaluation(Block block, LayoutResult layout, PdfReading reading)
    {
        var items = new List<FullItem>();
        var amounts = block.Records.ToDictionary(r => r.Sequence);
        var expected = amounts.Keys.ToHashSet();
        var pages = new Dictionary<int, List<int>>();
        foreach (var page in layout.Pages)
        {
            foreach (var fragment in page.Fragments)
            {
                if (!expected.Contains(fragment.Seq))
                {
                    items.Add(new FullItem(1, page.Page, fragment.Seq, "unknown-sequence", false, "a fragment of a row that is not in the block"));
                    continue;
                }

                if (!pages.TryGetValue(fragment.Seq, out var list))
                {
                    list = [];
                    pages[fragment.Seq] = list;
                }

                list.Add(page.Page);
            }
        }

        foreach (var seq in expected.Where(s => !pages.ContainsKey(s)).Order())
        {
            items.Add(new FullItem(1, null, seq, "missing-sequence", false, "no fragment"));
        }

        foreach (var (seq, list) in pages)
        {
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i] == list[i - 1] + 1)
                {
                    continue;
                }

                var gap = Enumerable.Range(list[i - 1] + 1, Math.Max(0, list[i] - list[i - 1] - 1)).ToList();
                var headerOnly = gap.Count > 0 && gap.All(p => reading.HeaderOnly[p - 1]);
                items.Add(new FullItem(1, list[i], seq, headerOnly ? "header-only-gap" : "non-contiguous", headerOnly,
                    gap.Count == 0 ? $"page {list[i]} repeated" : $"pages {gap[0]}–{gap[^1]} between fragments"));
            }
        }

        if (layout.Total != layout.Pages.Count)
        {
            items.Add(new FullItem(1, null, null, "page-count", false, $"reported total {layout.Total} but {layout.Pages.Count} pages read"));
        }

        // Rule 4.
        var fragmentsOf = layout.Pages.SelectMany(p => p.Fragments.Select(f => (Page: p.Page, Fragment: f))).GroupBy(x => x.Fragment.Seq).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var page in layout.Pages)
        {
            foreach (var f in page.Fragments.Where(f => amounts.ContainsKey(f.Seq)))
            {
                var isLast = f.Fragment == f.Fragments - 1;
                var text = amounts[f.Seq].AmountText;
                if (f.AmountBefore == (isLast ? text : string.Empty))
                {
                    continue;
                }

                var shown = fragmentsOf[f.Seq].Where(x => x.Fragment.AmountBefore.Length > 0).ToList();
                var placement = shown.Count == 1 && shown[0].Fragment.AmountBefore == text && shown[0].Fragment.Fragment != shown[0].Fragment.Fragments - 1;
                items.Add(new FullItem(4, page.Page, f.Seq, placement ? "amount-not-on-last-page" : "amount-wrong", placement,
                    $"fragment {f.Fragment + 1}/{f.Fragments} shows '{f.AmountBefore}', expected '{(isLast ? text : string.Empty)}'"));
            }
        }

        // Rule 5.
        foreach (var page in layout.Pages)
        {
            var expectedOnPage = 0;
            foreach (var f in page.Fragments)
            {
                var isLast = f.Fragment == f.Fragments - 1;
                if (f.Mark == isLast)
                {
                    items.Add(new FullItem(5, page.Page, f.Seq, isLast ? "mark-on-last-fragment" : "mark-missing", false, $"fragment {f.Fragment + 1}/{f.Fragments} mark={f.Mark}"));
                }

                if (!isLast)
                {
                    expectedOnPage++;
                }
            }

            if (page.Marks != expectedOnPage)
            {
                var covering = reading.RowPages.Where(kv => kv.Value.Min < page.Page && kv.Value.Max > page.Page).Select(kv => kv.Key).ToList();
                var placement = reading.HeaderOnly[page.Page - 1] && page.Marks == 1 && expectedOnPage == 0 && covering.Count == 1;
                items.Add(new FullItem(5, page.Page, covering.Count == 1 ? covering[0] : null, placement ? "mark-on-header-only-page" : "mark-count", placement,
                    $"{page.Marks} marks on the page, {expectedOnPage} expected by the word-based reading"));
            }
        }

        return items;
    }

    /// <summary>TI failures of one PDF: placement causes listed by the plan; everything else refutes (nothing is hidden).</summary>
    private static void ClassifyIntegrity(AccountingEntry entry, IntegrityEntry? ti, PdfReading reading, string which, bool required)
    {
        if (ti is null)
        {
            if (required)
            {
                entry.Outcome = $"inconclusive (no text-integrity record for {which})";
            }

            return;
        }

        if (ti.Verdict is "no-pdf" or "no-check")
        {
            entry.Outcome = $"inconclusive (text integrity {ti.Verdict} for {which})";
            return;
        }

        if (ti.Verdict == "pass")
        {
            return;
        }

        var failures = ti.Failures ?? [];
        if (ti.FailureCount > failures.Count)
        {
            entry.Refuting.Add($"{which}: {ti.FailureCount - failures.Count} text-integrity failures beyond the {failures.Count} listed");
        }

        var headerOnlyMarks = 0;
        for (var page = 1; page <= reading.Pages; page++)
        {
            if (reading.HeaderOnly[page - 1] && reading.RowPages.Values.Any(p => p.Min < page && p.Max > page))
            {
                headerOnlyMarks += reading.MarksOn(page).Footer + reading.MarksOn(page).Content;
            }
        }

        foreach (var f in failures)
        {
            var text = $"{which} {f.Rule} {f.Class}" + (f.Row is null ? string.Empty : $" row {f.Row}") + (f.Page is null ? string.Empty : $" p{f.Page}") + (f.Detail is null ? string.Empty : $": {f.Detail}");
            entry.TiFailures.Add(text);
            if (f.Rule == "TI-7" && f.Detail == "the amount is not on the row's last page")
            {
                entry.Placement.Add($"{which} TI-7 amount-not-on-last-page row {f.Row}");
            }
            else if (f.Rule == "TI-7" && f.Detail is not null && f.Detail.EndsWith(" mark(s) where no row continues", StringComparison.Ordinal)
                && int.TryParse(f.Detail.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var extra) && extra == headerOnlyMarks)
            {
                entry.Placement.Add($"{which} TI-7 mark-on-header-only-page ({extra} marks on header-only pages inside a row's span)");
            }
            else
            {
                entry.Refuting.Add(text);
            }
        }
    }

    /// <summary>O-E, O-A, O-S and the footer-text hash of O-D, from the pass-2 reading.</summary>
    private static void Observe(AccountingEntry entry, PdfReading reading, LayoutResult layout)
    {
        var footerText = new StringBuilder();
        foreach (var page in layout.Pages)
        {
            footerText.Append(page.Page).Append('|').Append(page.Footer.Left).Append('|').Append(page.Footer.Center).Append('|').Append(page.Footer.Right).Append('|').Append(page.Marks).Append('\n');
            if (!reading.WithoutDetail[page.Page - 1])
            {
                continue;
            }

            var brought = Value(page.Footer.Left, "Brought forward: ");
            var subtotal = Value(page.Footer.Center, "Page subtotal: ");
            var carried = Value(page.Footer.Right, "Carried forward: ");
            var headerOnly = reading.HeaderOnly[page.Page - 1];
            if (headerOnly)
            {
                entry.HeaderOnlyPages++;
            }

            entry.PagesWithoutDetail.Add(new PageWithoutDetail(
                page.Page,
                headerOnly ? "header-only" : "free-text",
                page.Footer.Left,
                page.Footer.Center,
                page.Footer.Right,
                reading.MarksOn(page.Page).Footer > 0,
                subtotal is null ? null : subtotal == 0m,
                brought is null || carried is null ? null : brought == carried,
                [.. reading.RowPages.Where(kv => kv.Value.Min <= page.Page && kv.Value.Max >= page.Page).Select(kv => kv.Key)]));
        }

        entry.FooterTextsSha256 = Sha(footerText.ToString());
        foreach (var (seq, pages) in reading.RowPages.Where(kv => kv.Value.Count > 1 || kv.Value.Max > kv.Value.Min))
        {
            var amountWords = reading.AmountWords(seq);
            int? amountPage = amountWords.Count > 0 ? amountWords[^1].Word.Page : null;
            var position = amountPage is null ? null : amountPage == pages.Max ? "last" : amountPage == pages.Min ? "first" : "other";
            bool? onLastLine = amountWords.Count > 0 ? amountWords[^1].Line == reading.LastLine[seq] : null;
            entry.AmountPlacements.Add(new AmountPlacement(seq, pages.Min, pages.Max, amountPage, position, onLastLine));
        }

        entry.SplitRows = entry.AmountPlacements.Count;
        entry.RowsOverThreePages = entry.AmountPlacements.Count(a => a.LastPage - a.FirstPage + 1 >= 3);
        entry.LargestSpan = entry.AmountPlacements.Count == 0 ? 1 : entry.AmountPlacements.Max(a => a.LastPage - a.FirstPage + 1);
    }

    /// <summary>O-M: markers against the layout — pages, per-page word counts ("cell") and spans against the reference run of the same fixture and markup.</summary>
    private static void ObserveMarkersVersusLayout(AccountingEntry entry, TwoPassLine run, PdfReading pass1, IReadOnlyDictionary<string, IntegrityEntry>? reference)
    {
        var key = $"{run.FixtureId}-{run.Markup}-r1";
        if (reference is null || !reference.TryGetValue(key, out var basis) || run.Mutation is not null)
        {
            entry.MarkersVersusLayout["reference"] = null;
            return;
        }

        var spans = string.Join(",", pass1.RowPages.Where(kv => kv.Value.Max > kv.Value.Min).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.Min}-{kv.Value.Max}"));
        var basisSpans = string.Join(",", (basis.Observations?.MultiPageRows ?? []).OrderBy(s => s.Row).Select(s => $"{s.Row}:{s.FirstPage}-{s.LastPage}"));
        entry.MarkersVersusLayout["reference"] = key;
        entry.MarkersVersusLayout["pages"] = pass1.Pages == basis.Pages ? "equal" : $"{pass1.Pages} against {basis.Pages}";
        entry.MarkersVersusLayout["spans"] = spans == basisSpans ? "equal" : $"{spans} against {basisSpans}";
        if (run.Strategy == "cell")
        {
            entry.MarkersVersusLayout["pageWordCounts"] = basis.PageWordCounts is not null && entry.Pass1PageWordCounts!.SequenceEqual(basis.PageWordCounts) ? "equal" : "differ";
        }
    }

    private static decimal? Value(string? text, string prefix) =>
        text is not null && text.StartsWith(prefix, StringComparison.Ordinal)
        && decimal.TryParse(text[prefix.Length..], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string RowOf(string detail) => detail.StartsWith("row ", StringComparison.Ordinal) ? detail.Split(' ', ':')[1].TrimEnd(':') : detail;

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
