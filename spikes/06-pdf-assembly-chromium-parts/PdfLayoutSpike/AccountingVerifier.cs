using System.Globalization;

namespace PdfLayoutSpike;

/// <summary>The failure-signature evaluation of one run (S1 to S3, each with its evidence).</summary>
internal sealed record SignatureEvaluation(
    string Configuration,
    string RunId,
    bool S1,
    string S1Evidence,
    bool S2,
    string S2Evidence,
    bool S3,
    string S3Evidence,
    bool Signature);

/// <summary>Outcome of one verification check of one run.</summary>
internal sealed record CheckResult(string Name, bool Pass, int? FirstFailingPage, string? Detail)
{
    public string Outcome => Pass ? "PASS" : "FAIL";
}

/// <summary>
/// Checks a finished layout against the block's own records — amounts are taken
/// from the dataset as decimals, never from the page — independently of the
/// in-page script that wrote the footer.
/// </summary>
internal static class AccountingVerifier
{
    public const string Rows = "rows-not-lost-or-duplicated";
    public const string EndPageSubtotals = "end-page-subtotals";
    public const string Chain = "brought-carried-chain";
    public const string VisibleAmounts = "visible-amounts";
    public const string Marks = "continues-marks";
    public const string SplitAttributes = "split-attributes-agree";
    public const string Geometry = "geometry";
    public const string FooterNoDisplacement = "footer-does-not-displace-rows";
    public const string Escaping = "escaping-and-isolation";
    public const string Determinism = "deterministic-layout";
    public const string ColumnAlignment = "column-alignment-observation";
    public const string DescriptionText = "split-row-text-observation";
    public const string SplitRows = "rows-spanning-pages-observation";

    /// <summary>The console text the pagination library logs when it gives up on a layout loop.</summary>
    public const string LayoutRepeated = "Layout repeated";

    /// <summary>The checks of the end-page rule (accounting) — those that select the primary configuration.</summary>
    public static readonly IReadOnlySet<string> AccountingChecks = new HashSet<string>(StringComparer.Ordinal)
    {
        Rows, EndPageSubtotals, Chain, VisibleAmounts, Marks,
    };

    public static List<CheckResult> VerifyPaged(Block block, RunResult run)
    {
        var layout = run.Layout ?? throw new InvalidOperationException("No layout result.");
        var footerOn = run.Footer;
        var checks = new List<CheckResult>
        {
            CheckRows(block, layout),
        };

        var amounts = block.Records.ToDictionary(r => r.Sequence);
        var lastPage = LastPageOfSequence(layout);
        if (footerOn)
        {
            checks.Add(CheckSubtotals(block, layout, amounts, lastPage));
            checks.Add(CheckChain(block, layout));
            checks.Add(CheckVisibleAmounts(layout, amounts));
            checks.Add(CheckMarks(layout));
            if (layout.Pages.Any(p => p.Fragments.Any(f => f.SplitFrom || f.SplitTo)))
            {
                checks.Add(CheckSplitAttributes(layout));
            }
        }

        checks.Add(CheckGeometry(layout, footerOn));
        checks.Add(CheckEscaping(run));
        checks.Add(ObserveAlignment(layout));
        checks.Add(ObserveDescriptionText(layout, amounts));
        checks.Add(ObserveSplitRows(layout, amounts));
        return checks;
    }

    /// <summary>
    /// Rules 1 to 5 of the end-page rule (rows, subtotals, chain, visible amounts,
    /// marks — the split-attribute diagnostic being part of rule 5 when the
    /// attributes exist) all present and passing.
    /// </summary>
    public static bool PassesEndPageRules(RunResult run) =>
        run.Status == "ok"
        && AccountingChecks.All(name => run.Checks.Any(c => c.Name == name && c.Pass))
        && run.Checks.Where(c => c.Name == SplitAttributes).All(c => c.Pass);

    /// <summary>Whether the page console holds an error containing the library's layout-loop message.</summary>
    public static bool LogsLayoutRepeated(RunResult run) =>
        run.ConsoleMessages.Any(m => m.StartsWith("error:", StringComparison.Ordinal) && m.Contains(LayoutRepeated, StringComparison.Ordinal));

    /// <summary>
    /// The documented failure signature of a block with one row taller than the
    /// layout can handle: S1 the run completes with status ok (the library resolves
    /// its preview); S2 the console holds an error containing "Layout repeated";
    /// S3 rule 1 fails only by missing sequences, and the missing set is exactly
    /// every item sequence of the block after the tall record.
    /// </summary>
    public static SignatureEvaluation EvaluateSignature(Block block, RunResult run, int tallSequence)
    {
        var s1 = run.Status == "ok" && run.Layout is not null;
        var s1Evidence = $"status {run.Status}{(run.Message is null ? string.Empty : ", " + run.Message)}";
        var s2 = LogsLayoutRepeated(run);
        var repeated = run.ConsoleMessages.Where(m => m.Contains(LayoutRepeated, StringComparison.Ordinal)).ToList();
        var s2Evidence = s2 ? $"{repeated.Count} console error(s): {repeated[0]}" : $"no console error containing '{LayoutRepeated}' ({run.ConsoleMessages.Count} console messages)";

        bool s3;
        string s3Evidence;
        if (run.Layout is null)
        {
            s3 = false;
            s3Evidence = "no layout result";
        }
        else
        {
            var coverage = Coverage(block, run.Layout);
            var rule1 = run.Checks.FirstOrDefault(c => c.Name == Rows);
            var tallIndex = block.Records.ToList().FindIndex(r => r.Sequence == tallSequence);
            var expectedMissing = block.Records.Skip(tallIndex + 1).Select(r => r.Sequence).ToList();
            var missingSet = coverage.Missing.ToHashSet();
            s3 = tallIndex >= 0
                && rule1 is { Pass: false }
                && coverage.Unknown.Count == 0
                && coverage.Duplicated.Count == 0
                && missingSet.SetEquals(expectedMissing);
            var missingText = coverage.Missing.Count == 0 ? "none" : $"{coverage.Missing.Count} ({coverage.Missing[0]}–{coverage.Missing[^1]})";
            s3Evidence = $"rule 1 {rule1?.Outcome ?? "not run"} '{rule1?.Detail}'; missing {missingText}; expected missing {expectedMissing.Count} (every sequence after {tallSequence}); "
                + $"unknown {coverage.Unknown.Count}; duplicated or non-contiguous {coverage.Duplicated.Count}";
        }

        return new SignatureEvaluation(run.Configuration, run.Id, s1, s1Evidence, s2, s2Evidence, s3, s3Evidence, s1 && s2 && s3);
    }

    /// <summary>The pages each item sequence appears on, in page order.</summary>
    public static Dictionary<int, List<int>> PagesOfSequence(LayoutResult layout)
    {
        var pages = new Dictionary<int, List<int>>();
        foreach (var page in layout.Pages)
        {
            foreach (var fragment in page.Fragments)
            {
                if (!pages.TryGetValue(fragment.Seq, out var list))
                {
                    list = [];
                    pages[fragment.Seq] = list;
                }

                list.Add(page.Page);
            }
        }

        return pages;
    }

    /// <summary>Missing, unknown and duplicated (twice on a page, or on non-contiguous pages) sequences.</summary>
    private static (List<int> Missing, List<int> Unknown, List<int> Duplicated) Coverage(Block block, LayoutResult layout)
    {
        var expected = block.Records.Select(r => r.Sequence).ToHashSet();
        var pages = PagesOfSequence(layout);
        var missing = expected.Where(s => !pages.ContainsKey(s)).Order().ToList();
        var unknown = pages.Keys.Where(s => !expected.Contains(s)).Order().ToList();
        var duplicated = pages.Where(kv => kv.Value.Zip(kv.Value.Skip(1)).Any(p => p.Second != p.First + 1)).Select(kv => kv.Key).Order().ToList();
        return (missing, unknown, duplicated);
    }

    public static List<CheckResult> VerifyNative(RunResult run) => [CheckEscaping(run)];

    /// <summary>Same page count and, page by page, the same ordered fragments (sequence and fragment index).</summary>
    public static CheckResult CompareLayouts(string name, LayoutResult a, LayoutResult b)
    {
        if (a.Pages.Count != b.Pages.Count)
        {
            return new CheckResult(name, false, Math.Min(a.Pages.Count, b.Pages.Count) + 1, $"page count {a.Pages.Count} vs {b.Pages.Count}");
        }

        for (var i = 0; i < a.Pages.Count; i++)
        {
            var left = a.Pages[i].Fragments.Select(f => (f.Seq, f.Fragment));
            var right = b.Pages[i].Fragments.Select(f => (f.Seq, f.Fragment));
            if (!left.SequenceEqual(right))
            {
                return new CheckResult(name, false, i + 1, "fragment lists differ");
            }
        }

        return new CheckResult(name, true, null, $"{a.Pages.Count} pages identical");
    }

    private static Dictionary<int, int> LastPageOfSequence(LayoutResult layout)
    {
        var last = new Dictionary<int, int>();
        foreach (var page in layout.Pages)
        {
            foreach (var fragment in page.Fragments)
            {
                last[fragment.Seq] = page.Page;
            }
        }

        return last;
    }

    private static CheckResult CheckRows(Block block, LayoutResult layout)
    {
        var expected = block.Records.Select(r => r.Sequence).ToHashSet();
        var seen = new Dictionary<int, List<int>>();
        foreach (var page in layout.Pages)
        {
            foreach (var fragment in page.Fragments)
            {
                if (!expected.Contains(fragment.Seq))
                {
                    return new CheckResult(Rows, false, page.Page, $"unknown sequence {fragment.Seq}");
                }

                if (!seen.TryGetValue(fragment.Seq, out var pages))
                {
                    pages = [];
                    seen[fragment.Seq] = pages;
                }

                if (pages.Count > 0 && page.Page != pages[^1] + 1)
                {
                    return new CheckResult(Rows, false, page.Page, $"sequence {fragment.Seq} on pages {string.Join(',', pages)} and {page.Page}");
                }

                pages.Add(page.Page);
            }
        }

        var missing = expected.Where(s => !seen.ContainsKey(s)).Order().ToList();
        if (missing.Count > 0)
        {
            return new CheckResult(Rows, false, null, $"{missing.Count} sequences missing, first {missing[0]}");
        }

        if (layout.Total != layout.Pages.Count)
        {
            return new CheckResult(Rows, false, null, $"reported total {layout.Total} but {layout.Pages.Count} pages read");
        }

        var split = seen.Count(kv => kv.Value.Count > 1);
        return new CheckResult(Rows, true, null, $"{expected.Count} rows on {layout.Pages.Count} pages, {split} split");
    }

    private static CheckResult CheckSubtotals(Block block, LayoutResult layout, Dictionary<int, DatasetRecord> amounts, Dictionary<int, int> lastPage)
    {
        var zeroPages = 0;
        foreach (var page in layout.Pages)
        {
            var expected = lastPage.Where(kv => kv.Value == page.Page).Sum(kv => amounts[kv.Key].Amount);
            if (!TryParse(page.Footer.Center, "Page subtotal: ", out var printed))
            {
                return new CheckResult(EndPageSubtotals, false, page.Page, $"unreadable footer '{page.Footer.Center}'");
            }

            if (printed != expected)
            {
                return new CheckResult(EndPageSubtotals, false, page.Page, $"printed {printed} expected {expected}");
            }

            if (!lastPage.ContainsValue(page.Page))
            {
                zeroPages++;
                if (page.Footer.Center != "Page subtotal: 0.00")
                {
                    return new CheckResult(EndPageSubtotals, false, page.Page, "page without a row ending does not show 0.00");
                }
            }
        }

        return new CheckResult(EndPageSubtotals, true, null, $"{layout.Pages.Count} pages, {zeroPages} with no row ending (0.00); block total {block.Total.ToString("0.00", CultureInfo.InvariantCulture)}");
    }

    private static CheckResult CheckChain(Block block, LayoutResult layout)
    {
        decimal previousCarried = 0m;
        foreach (var page in layout.Pages)
        {
            if (!TryParse(page.Footer.Left, "Brought forward: ", out var brought)
                || !TryParse(page.Footer.Center, "Page subtotal: ", out var subtotal)
                || !TryParse(page.Footer.Right, "Carried forward: ", out var carried))
            {
                return new CheckResult(Chain, false, page.Page, "unreadable footer");
            }

            if (brought != previousCarried)
            {
                return new CheckResult(Chain, false, page.Page, $"brought forward {brought} expected {previousCarried}");
            }

            if (carried != brought + subtotal)
            {
                return new CheckResult(Chain, false, page.Page, $"carried forward {carried} expected {brought + subtotal}");
            }

            previousCarried = carried;
        }

        return previousCarried == block.Total
            ? new CheckResult(Chain, true, null, $"starts at 0.00, ends at {previousCarried.ToString("0.00", CultureInfo.InvariantCulture)} = block total")
            : new CheckResult(Chain, false, layout.Pages.Count, $"ends at {previousCarried} but block total is {block.Total}");
    }

    private static CheckResult CheckVisibleAmounts(LayoutResult layout, Dictionary<int, DatasetRecord> amounts)
    {
        foreach (var page in layout.Pages)
        {
            foreach (var f in page.Fragments)
            {
                var isLast = f.Fragment == f.Fragments - 1;
                var expected = isLast ? amounts[f.Seq].AmountText : string.Empty;
                if (f.AmountBefore != expected)
                {
                    return new CheckResult(VisibleAmounts, false, page.Page, $"sequence {f.Seq} fragment {f.Fragment + 1}/{f.Fragments} shows '{f.AmountBefore}', expected '{expected}'");
                }
            }
        }

        return new CheckResult(VisibleAmounts, true, null, "each amount printed only on the fragment where its row ends");
    }

    private static CheckResult CheckMarks(LayoutResult layout)
    {
        var total = 0;
        foreach (var page in layout.Pages)
        {
            var expectedOnPage = 0;
            foreach (var f in page.Fragments)
            {
                var isLast = f.Fragment == f.Fragments - 1;
                if (f.Mark == isLast)
                {
                    return new CheckResult(Marks, false, page.Page, $"sequence {f.Seq} fragment {f.Fragment + 1}/{f.Fragments} mark={f.Mark}");
                }

                if (!isLast)
                {
                    expectedOnPage++;
                }
            }

            if (page.Marks != expectedOnPage)
            {
                return new CheckResult(Marks, false, page.Page, $"{page.Marks} marks on the page, {expectedOnPage} expected");
            }

            total += expectedOnPage;
        }

        return new CheckResult(Marks, true, null, $"{total} marks, all on non-final fragments");
    }

    private static CheckResult CheckSplitAttributes(LayoutResult layout)
    {
        foreach (var page in layout.Pages)
        {
            foreach (var f in page.Fragments)
            {
                var continues = f.Fragment < f.Fragments - 1;
                var continued = f.Fragment > 0;
                if (f.SplitTo != continues || f.SplitFrom != continued)
                {
                    return new CheckResult(SplitAttributes, false, page.Page, $"sequence {f.Seq} fragment {f.Fragment + 1}/{f.Fragments}: split-to={f.SplitTo} split-from={f.SplitFrom}");
                }
            }
        }

        return new CheckResult(SplitAttributes, true, null, "split-to/split-from attributes agree with the end-page rule on every page");
    }

    private static CheckResult CheckGeometry(LayoutResult layout, bool footerOn)
    {
        foreach (var page in layout.Pages)
        {
            if (page.BelowArea.Count > 0)
            {
                return new CheckResult(Geometry, false, page.Page, $"sequence {page.BelowArea[0]} extends below the content area");
            }

            if (footerOn && page.FooterOverflow.Count > 0)
            {
                return new CheckResult(Geometry, false, page.Page, $"footer box {page.FooterOverflow[0]} overflows");
            }
        }

        if (footerOn && layout.MarginBoxesFound != true)
        {
            return new CheckResult(Geometry, false, null, "bottom margin boxes not found");
        }

        return new CheckResult(Geometry, true, null, footerOn ? "no fragment below the content area, no footer overflow" : "no fragment below the content area");
    }

    private static CheckResult CheckEscaping(RunResult run)
    {
        var pass = run.Dialogs == 0 && run.AbortedRequests == 0 && run.ScriptsFound == run.ScriptsExpected;
        return new CheckResult(Escaping, pass, null, $"dialogs {run.Dialogs}, aborted requests {run.AbortedRequests}, not found {run.NotFoundRequests}, scripts {run.ScriptsFound}/{run.ScriptsExpected}");
    }

    private static CheckResult ObserveAlignment(LayoutResult layout)
    {
        var fragments = layout.Pages.SelectMany(p => p.Fragments).Where(f => f.Fragments > 1).ToList();
        var aligned = fragments.Count(f => f.Aligned == true);
        // An observation, reported but never failing.
        return new CheckResult(ColumnAlignment, true, null, $"{aligned}/{fragments.Count} fragments of split rows keep their cells in their columns");
    }

    /// <summary>
    /// Observation: for each split row, the description characters shown across its
    /// fragments against the record's own description (white space may differ at
    /// the break points). Reported, never failing.
    /// </summary>
    private static CheckResult ObserveDescriptionText(LayoutResult layout, Dictionary<int, DatasetRecord> records)
    {
        var shown = layout.Pages.SelectMany(p => p.Fragments)
            .Where(f => f.Fragments > 1)
            .GroupBy(f => f.Seq)
            .Select(g => (Seq: g.Key, Shown: g.Sum(f => f.DescChars), Expected: records.TryGetValue(g.Key, out var r) ? r.Description.Length : 0))
            .ToList();
        var differing = shown.Where(s => Math.Abs(s.Shown - s.Expected) > 2 * layout.Pages.Count).ToList();
        var detail = shown.Count == 0
            ? "no split rows"
            : string.Join("; ", shown.Select(s => $"sequence {s.Seq}: {s.Shown} of {s.Expected} characters shown"));
        return new CheckResult(DescriptionText, true, null, differing.Count == 0 ? detail : $"{differing.Count} split rows differ — {detail}");
    }

    /// <summary>
    /// Observation: how many rows span two or more pages, the longest description
    /// among them and the largest number of pages any one of them occupies.
    /// Reported, never failing.
    /// </summary>
    private static CheckResult ObserveSplitRows(LayoutResult layout, Dictionary<int, DatasetRecord> records)
    {
        var spanning = PagesOfSequence(layout)
            .Where(kv => kv.Value.Distinct().Count() > 1)
            .Select(kv => (Seq: kv.Key, Pages: kv.Value.Distinct().Count(), Length: records.TryGetValue(kv.Key, out var r) ? r.Description.Length : 0))
            .ToList();
        var detail = spanning.Count == 0
            ? "0 rows span two or more pages"
            : $"{spanning.Count} rows span two or more pages; longest description among them {spanning.Max(s => s.Length)} characters; largest span {spanning.Max(s => s.Pages)} pages";
        return new CheckResult(SplitRows, true, null, detail);
    }

    private static bool TryParse(string? text, string prefix, out decimal value)
    {
        value = 0m;
        return text is not null
            && text.StartsWith(prefix, StringComparison.Ordinal)
            && decimal.TryParse(text[prefix.Length..], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
