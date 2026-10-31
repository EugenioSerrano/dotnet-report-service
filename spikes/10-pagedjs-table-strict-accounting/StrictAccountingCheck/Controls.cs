using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using TextIntegrityCheck;

namespace StrictAccountingCheck;

/// <summary>One control: what was expected, what was observed, and whether it behaved (yes, no, not evaluable).</summary>
internal sealed record ControlItem(string Group, string Id, string Run, string Expected, List<string> Observed, string Behaved, string? Note = null);

/// <summary>
/// The "controls" command. A control behaves only when its observed failures are
/// exactly its expected set; a control expecting no failure must show none. The
/// text oracle's own "any failure matches" result is kept as a note, never as the
/// result. Groups: the text oracle in grid (must behave) and in table (reported);
/// the strict check's positive and page-level controls and its extraction-level
/// faults AX-1 … AX-4; the character check's agreement with the text oracle in
/// grid and its own controls in table; the probe's known answers.
/// </summary>
internal static class Controls
{
    public static async Task<int> RunAsync(
        string fixturesDirectory, IReadOnlyList<string> runsDirectories, IReadOnlyList<string> integrityPaths, string accountingPath,
        string gridExtractionPath, string? tableExtractionPath, string probePath, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var integrity = new Dictionary<string, IntegrityEntry>(StringComparer.Ordinal);
        foreach (var path in integrityPaths)
        {
            foreach (var (id, e) in Files.ReadIntegrity(path))
            {
                integrity[id] = e;
            }
        }

        var accounting = ReadAccounting(accountingPath);
        var stages = runsDirectories.Select(StageFolder.Load).ToList();
        var items = new List<ControlItem>();

        foreach (var markup in new[] { "grid", "table" })
        {
            var group = markup == "grid" ? "text-grid" : "text-table";
            TextControls(markup, group, accounting, integrity, items);
            var extraction = markup == "grid" ? gridExtractionPath : tableExtractionPath;
            ExtractionTextControls(group, extraction, items);
            StrictControls(markup, accounting, items);
            var ax = await FaultControlsAsync(markup, accounting, stages, fixturesDirectory, sums, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            items.AddRange(ax);
        }

        CharacterAgreement(accounting, integrity, items);
        CharacterTableControls(accounting, items);
        ProbeItems(probePath, items);

        bool Behaves(string group) => items.Where(i => i.Group == group).All(i => i.Behaved != "no");
        var textGrid = Behaves("text-grid") && items.Any(i => i.Group == "text-grid" && i.Behaved == "yes");
        var strictGrid = Behaves("strict-grid") && Behaves("faults-grid") && items.Where(i => i.Group == "faults-grid").Count(i => i.Behaved == "yes") >= 3
            && items.Any(i => i.Group == "strict-table" && i.Id == "positive" && i.Run.Contains("-K1-", StringComparison.Ordinal) && i.Behaved == "yes");
        var characterGrid = Behaves("character-agreement");
        var probe = Behaves("probe") && items.Any(i => i.Group == "probe");
        var tableCt = items.Where(i => i.Group == "character-table").ToList();
        var ctProven = new[] { "NC-W", "NC-L", "NC-R" }.All(m => tableCt.Any(i => i.Id == m && i.Behaved == "yes"))
            && tableCt.All(i => i.Behaved != "no");
        var trusted = textGrid && strictGrid && characterGrid && probe;

        foreach (var i in items)
        {
            Console.WriteLine($"{i.Group} {i.Id} on {i.Run}: {i.Behaved} — expected [{i.Expected}], observed [{string.Join(" | ", i.Observed)}]{(i.Note is null ? string.Empty : $" ({i.Note})")}");
        }

        Console.WriteLine($"Instrument trusted: {trusted} (text grid {textGrid}, strict grid {strictGrid}, character agreement {characterGrid}, probe {probe}); "
            + $"character check control-proven in table: {ctProven}.");
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "controls.json"), new
        {
            trusted,
            textGrid,
            strictGrid,
            characterAgreementGrid = characterGrid,
            probe,
            characterControlProvenTable = ctProven,
            items,
        });
        return 0;
    }

    public static List<StrictEntry> ReadAccounting(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        return node["runs"]!.Deserialize<List<StrictEntry>>(StageFolder.Json) ?? [];
    }

    private static StrictEntry? Find(List<StrictEntry> accounting, string markup, string fixture, string? mutation, bool footerOn = true) =>
        accounting.FirstOrDefault(e => e.Markup == markup && e.FixtureId == fixture && e.Mutation == mutation && e.FooterOn == footerOn
            && e.Split == "split" && e.PartLimit == 0.9 && e.Repetition == 1 && e.MatrixRow is 1 or 9 or 12);

    /// <summary>Positive controls B100, K1; NC-W, NC-L, NC-R on B100 and T300u; NC-F on F26K-a — by the sole-failure rule.</summary>
    private static void TextControls(string markup, string group, List<StrictEntry> accounting, Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        foreach (var fixture in new[] { "B100", "K1" })
        {
            var e = Find(accounting, markup, fixture, null);
            var ti = e is null ? null : integrity.GetValueOrDefault(e.Run);
            items.Add(new ControlItem(group, "positive", e?.Run ?? fixture, "pass", Observed(ti), ti is null ? "no" : ti.Verdict == "pass" ? "yes" : markup == "table" && fixture == "B100" ? "not evaluable" : "no",
                markup == "table" && fixture == "B100" ? "B100 in table crosses into pages whose geometry is the effect under measurement: reported" : null));
        }

        foreach (var (fixture, mutations) in new[] { ("B100", new[] { "NC-W", "NC-L", "NC-R" }), ("T300u", new[] { "NC-W", "NC-L", "NC-R" }), ("F26K-a", new[] { "NC-F" }) })
        {
            var baseEntry = accounting.FirstOrDefault(e => e.Markup == markup && e.FixtureId == fixture && e.Mutation is null && e.FooterOn && e.Split == "split" && e.PartLimit == 0.9 && e.Repetition == 1);
            var baseTi = baseEntry is null ? null : integrity.GetValueOrDefault(baseEntry.Run);
            foreach (var mutation in mutations)
            {
                var e = Find(accounting, markup, fixture, mutation);
                var ti = e is null ? null : integrity.GetValueOrDefault(e.Run);
                var (cls, row, region) = ControlMutations.Expected(mutation);
                var expected = $"{cls}{(row is null ? string.Empty : $" row {row}")}{(region is null ? string.Empty : $" {region}")}";
                var sole = ti is not null && ti.FailureCount == 1 && ti.Failures is [var only] && only.Class == cls && (row is null || only.Row == row) && (region is null || only.Region == region);
                var any = ti?.Failures?.Any(f => f.Class == cls && (row is null || f.Row == row) && (region is null || f.Region == region)) == true;
                string behaved;
                if (baseTi is null || baseTi.Verdict != "pass")
                {
                    behaved = markup == "grid" ? "no" : "not evaluable";
                }
                else
                {
                    behaved = sole ? "yes" : "no";
                }

                items.Add(new ControlItem(group, mutation, e?.Run ?? $"{mutation} on {fixture}", expected, Observed(ti), behaved,
                    $"base {baseEntry?.Run} text {baseTi?.Verdict ?? "missing"}; any failure matches: {any}"));
            }
        }
    }

    /// <summary>E1-E8 of the text oracle's own controls command, by the sole-failure rule; E5-E8 not evaluable.</summary>
    private static void ExtractionTextControls(string group, string? path, List<ControlItem> items)
    {
        if (path is null || !File.Exists(path))
        {
            items.Add(new ControlItem(group, "E1-E4", "-", "each its one failure", [], group == "text-grid" ? "no" : "not evaluable", "no extraction-control file"));
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var baseRun = node["baseRun"]?.GetValue<string>();
        foreach (var c in node["controls"]!.AsArray())
        {
            var id = c!["id"]!.GetValue<string>();
            var observed = c["observed"]?.AsArray().Select(o => o!.GetValue<string>()).ToList() ?? [];
            var behaved = c["behaved"]!.GetValue<string>();
            var expected = c["expected"]!.GetValue<string>();
            if (id is "E5" or "E6" or "E7" or "E8")
            {
                items.Add(new ControlItem(group, id, baseRun ?? "-", expected, observed, "not evaluable",
                    $"the text oracle's controls cannot take real marks (they add simulated marks and evaluate without the footer); its own result: {behaved}"));
                continue;
            }

            var result = baseRun is null ? (group == "text-grid" ? "no" : "not evaluable")
                : behaved == "yes" && observed.Count == 1 ? "yes" : "no";
            items.Add(new ControlItem(group, id, baseRun ?? "-", expected, observed, result, $"base choice {node["baseChoice"]?.GetValue<string>()}; its own result: {behaved}"));
        }
    }

    /// <summary>Positive and page-level controls of the strict check.</summary>
    private static void StrictControls(string markup, List<StrictEntry> accounting, List<ControlItem> items)
    {
        var group = "strict-" + markup;
        foreach (var fixture in new[] { "B100", "K1" })
        {
            var e = Find(accounting, markup, fixture, null);
            var failures = e?.FailureSet ?? ["missing"];
            var required = markup == "grid" || fixture == "K1";
            items.Add(new ControlItem(group, "positive", e?.Run ?? fixture, "-", failures, e is { Verdict: "checked" } && failures.Count == 0 ? "yes" : required ? "no" : "not evaluable",
                required ? null : "B100 in table: reported"));
        }

        foreach (var fixture in new[] { "B100", "T300u", "F26K-a" })
        {
            var baseEntry = accounting.FirstOrDefault(e => e.Markup == markup && e.FixtureId == fixture && e.Mutation is null && e.FooterOn && e.Split == "split" && e.PartLimit == 0.9 && e.Repetition == 1);
            var baseClean = baseEntry is { Verdict: "checked", FailureSet.Count: 0 };
            foreach (var mutation in fixture == "F26K-a" ? new[] { "NC-F" } : ["NC-W", "NC-L", "NC-R"])
            {
                var e = Find(accounting, markup, fixture, mutation);
                var observed = e?.FailureSet ?? ["missing"];
                string expectedText;
                bool ok;
                if (mutation == "NC-R")
                {
                    expectedText = $"{PdfLayoutSpike.AccountingVerifier.Rows}|p (sequence 51 missing); {PdfLayoutSpike.AccountingVerifier.Chain}|p<last page>";
                    var rule1 = e?.Rules.FirstOrDefault(r => r.Name == PdfLayoutSpike.AccountingVerifier.Rows);
                    ok = e is { Verdict: "checked" } && observed.Count == 2
                        && observed.Contains($"{PdfLayoutSpike.AccountingVerifier.Rows}|p") && rule1?.Detail == "1 sequences missing, first 51"
                        && observed.Contains($"{PdfLayoutSpike.AccountingVerifier.Chain}|p{e.Pages}");
                }
                else
                {
                    expectedText = "-";
                    ok = e is { Verdict: "checked" } && observed.Count == 0;
                }

                var behaved = !baseClean ? (markup == "grid" ? "no" : "not evaluable") : ok ? "yes" : "no";
                items.Add(new ControlItem(group, mutation, e?.Run ?? $"{mutation} on {fixture}", expectedText, observed, behaved, $"base {baseEntry?.Run} failures {(baseEntry is null ? "missing" : baseEntry.FailureSet.Count.ToString(CultureInfo.InvariantCulture))}"));
            }
        }
    }

    /// <summary>AX-1 … AX-4: faults applied in memory to a copy of the parsed base document; each must fail exactly its set.</summary>
    private static async Task<List<ControlItem>> FaultControlsAsync(string markup, List<StrictEntry> accounting, List<StageFolder> stages, string fixturesDirectory, Dictionary<string, string> sums, TimeSpan timeout, CancellationToken cancellation)
    {
        var group = "faults-" + markup;
        var result = new List<ControlItem>();
        var candidates = new[] { ("G200", 5), ("B1000", 2) };
        StrictEntry? baseEntry = null;
        foreach (var (fixture, row) in candidates)
        {
            baseEntry = accounting.FirstOrDefault(e => e.Markup == markup && e.FixtureId == fixture && e.Mutation is null && e.FooterOn && e.Split == "split"
                && e.PartLimit == 0.9 && e.Repetition == 1 && e.MatrixRow == row && e.Verdict == "checked"
                && !e.FailureSet.Any(f => Account.RuleNumber(f.Split('|')[0]) <= 6));
            if (baseEntry is not null)
            {
                break;
            }
        }

        var required = markup == "grid";
        if (baseEntry is null)
        {
            foreach (var id in new[] { "AX-1", "AX-2", "AX-3", "AX-4" })
            {
                result.Add(new ControlItem(group, id, "-", "-", [], required ? "no" : "not evaluable", "no base with rules 1-6 passing"));
            }

            return result;
        }

        var stage = stages.First(s => s.Runs.ContainsKey(baseEntry.Run));
        var run = stage.Runs[baseEntry.Run];
        var fixtureFile = FixtureFile.ReadVerified(fixturesDirectory, baseEntry.FixtureId, sums);
        var text = await Poppler.ExtractAsync(Path.Combine(stage.Directory, run.Pdf!), timeout, cancellation);
        var original = PrintedDocument.From(text);
        var reading = PdfReading.From(original, fixtureFile, "expected", "accounting");
        var rows = PdfLayoutSpike.AccountingVerifier.Rows;
        var subtotals = PdfLayoutSpike.AccountingVerifier.EndPageSubtotals;
        var chain = PdfLayoutSpike.AccountingVerifier.Chain;
        var marks = PdfLayoutSpike.AccountingVerifier.Marks;

        // AX-1: page 2's printed page subtotal + 0.01.
        result.Add(Fault(group, "AX-1", baseEntry, fixtureFile, original, run, [$"{subtotals}|p2", $"{chain}|p2"], d => ShiftFooterValue(d, 2, "subtotal:")));

        // AX-2: page 3's printed brought-forward value + 0.01.
        result.Add(Fault(group, "AX-2", baseEntry, fixtureFile, original, run, [$"{chain}|p3"], d => ShiftFooterValue(d, 3, "forward:", first: true)));

        // AX-3: the "(continues)" word on the first page of the first split row removed.
        var split = reading.RowPages.Where(kv => kv.Value.Max > kv.Value.Min).OrderBy(kv => kv.Key).Select(kv => (Row: kv.Key, Page: kv.Value.Min)).FirstOrDefault();
        if (split.Row == 0)
        {
            result.Add(new ControlItem(group, "AX-3", baseEntry.Run, "-", [], "not evaluable", "the base has no split row"));
        }
        else
        {
            result.Add(Fault(group, "AX-3", baseEntry, fixtureFile, original, run, [$"{marks}|p{split.Page}"], d =>
            {
                for (var i = 0; i < d.ContentLines.Count; i++)
                {
                    if (reading.RowOf[i] == split.Row && d.ContentLines[i].Page == split.Page)
                    {
                        var removed = d.ContentLines[i].Words.RemoveAll(w => w.Text == PdfReading.Continues);
                        if (removed > 0)
                        {
                            return $"removed {removed} '(continues)' of row {split.Row} on page {split.Page}";
                        }
                    }
                }

                throw new InvalidOperationException($"no '(continues)' found for row {split.Row} on page {split.Page}");
            }));
        }

        // AX-4: every word of the first one-page row anchored on page 2 removed.
        var target = reading.RowOrder.FirstOrDefault(seq => reading.RowPages[seq] is { Count: 1 } p && p.Min == 2);
        result.Add(Fault(group, "AX-4", baseEntry, fixtureFile, original, run, [$"{rows}|p", $"{subtotals}|p2"], d =>
        {
            if (target == 0)
            {
                throw new InvalidOperationException("no one-page row anchored on page 2");
            }

            var kept = new List<Line>();
            for (var i = 0; i < d.ContentLines.Count; i++)
            {
                if (reading.RowOf[i] != target)
                {
                    kept.Add(d.ContentLines[i]);
                }
            }

            var removed = d.ContentLines.Count - kept.Count;
            d.ContentLines.Clear();
            d.ContentLines.AddRange(kept);
            return $"removed the {removed} lines of row {target}";
        }));

        return result;
    }

    private static ControlItem Fault(string group, string id, StrictEntry baseEntry, Fixture fixture, PrintedDocument original, HarnessRun run, List<string> expected, Func<PrintedDocument, string> mutate)
    {
        var copy = original.Clone();
        string applied;
        try
        {
            applied = mutate(copy);
        }
        catch (InvalidOperationException e)
        {
            return new ControlItem(group, id, baseEntry.Run, string.Join("; ", expected), [], "no", "the fault could not be applied: " + e.Message);
        }

        var entry = new StrictEntry { Run = baseEntry.Run + "+" + id, Markup = baseEntry.Markup, FooterOn = true, Repetition = 1 };
        Account.Judge(entry, PdfReading.From(copy, fixture, "expected", "accounting"), fixture, run, null);
        var observed = entry.FailureSet;
        var behaved = observed.Count == expected.Count && expected.All(observed.Contains) ? "yes" : "no";
        return new ControlItem(group, id, baseEntry.Run, string.Join("; ", expected), observed, behaved, applied);
    }

    /// <summary>Adds 0.01 to the printed value that follows a footer label word on a page (the first or the last such label).</summary>
    private static string ShiftFooterValue(PrintedDocument document, int page, string label, bool first = false)
    {
        var words = document.FooterWords[page - 1];
        var indexes = Enumerable.Range(0, words.Count - 1).Where(i => words[i].Text == label).ToList();
        if (indexes.Count == 0)
        {
            throw new InvalidOperationException($"no footer word '{label}' on page {page}");
        }

        var at = (first ? indexes[0] : indexes[^1]) + 1;
        var value = decimal.Parse(words[at].Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var shifted = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture);
        words[at] = words[at] with { Text = shifted };
        return $"page {page} footer value {words[at - 1].Text} {value.ToString("0.00", CultureInfo.InvariantCulture)} -> {shifted}";
    }

    /// <summary>In grid, the character check passes a row exactly when the text oracle has no TI-2, TI-3 or TI-4 failure on it.</summary>
    private static void CharacterAgreement(List<StrictEntry> accounting, Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        var compared = 0;
        foreach (var e in accounting.Where(e => e.Markup == "grid" && e.Verdict == "checked"))
        {
            var ti = integrity.GetValueOrDefault(e.Run);
            if (ti is null || ti.Verdict is not ("pass" or "fail"))
            {
                continue;
            }

            if (ti.FailureCount > (ti.Failures?.Count ?? 0))
            {
                items.Add(new ControlItem("character-agreement", "row-by-row", e.Run, "agreement", [$"{ti.FailureCount} text failures, only {ti.Failures?.Count ?? 0} listed"], "not evaluable", "the text oracle lists at most 100 failures: not compared row by row"));
                continue;
            }

            compared++;
            var tiRows = (ti.Failures ?? []).Where(f => f.Rule is "TI-2" or "TI-3" or "TI-4" && f.Row is not null).GroupBy(f => f.Row!.Value).ToDictionary(g => g.Key, g => g.Select(f => f.Class).ToList());
            var ctRows = e.CharacterFailures.ToDictionary(c => c.Row, c => c.Class);
            var disagreements = new List<string>();
            foreach (var row in tiRows.Keys.Union(ctRows.Keys).Order())
            {
                var tiFails = tiRows.TryGetValue(row, out var classes);
                var ctFails = ctRows.TryGetValue(row, out var ctClass);
                if (tiFails != ctFails)
                {
                    disagreements.Add($"row {row}: text {(tiFails ? string.Join(",", classes!) : "pass")}, character {(ctFails ? ctClass : "pass")}");
                }
                else if (tiFails && e.Mutation is not null && !classes!.Contains(ctClass!))
                {
                    disagreements.Add($"row {row}: text {string.Join(",", classes!)}, character {ctClass} (class differs on a control)");
                }
            }

            if (disagreements.Count > 0 || e.Mutation is not null)
            {
                items.Add(new ControlItem("character-agreement", e.Mutation ?? "row-by-row", e.Run, "agreement", disagreements, disagreements.Count == 0 ? "yes" : "no"));
            }
        }

        items.Add(new ControlItem("character-agreement", "summary", "grid", "agreement on every row", [$"{compared} grid runs compared"], compared > 0 ? "yes" : "no"));
    }

    /// <summary>NC-W, NC-L and NC-R in table, on B100 and T300u: each exactly its character failure, on a base that passes the character check.</summary>
    private static void CharacterTableControls(List<StrictEntry> accounting, List<ControlItem> items)
    {
        foreach (var fixture in new[] { "B100", "T300u" })
        {
            var baseEntry = accounting.FirstOrDefault(e => e.Markup == "table" && e.FixtureId == fixture && e.Mutation is null && e.FooterOn && e.Split == "split" && e.PartLimit == 0.9 && e.Repetition == 1);
            var baseClean = baseEntry is { Verdict: "checked", CharacterFailures.Count: 0 };
            foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
            {
                var e = Find(accounting, "table", fixture, mutation);
                var (cls, row, _) = ControlMutations.Expected(mutation);
                var expectedClass = cls == "row-lost" ? "row-lost" : cls;
                var observed = e?.CharacterFailures.Select(c => $"{c.Class} row {c.Row}").ToList() ?? ["missing"];
                var ok = e is { Verdict: "checked" } && e.CharacterFailures is [var only] && only.Class == expectedClass && only.Row == row;
                var rowPages = e?.MultiPageRows.FirstOrDefault(s => s.Row == row) is { } span ? Enumerable.Range(span.FirstPage, span.LastPage - span.FirstPage + 1).ToList() : null;
                var geometry = e is null ? "missing" : rowPages is null ? "row on one page (or lost)" : string.Join(",", rowPages.Select(p => $"p{p} {e.Geometry.FirstOrDefault(g => g.Page == p)?.Class}"));
                items.Add(new ControlItem("character-table", mutation, e?.Run ?? $"{mutation} on {fixture}", $"{expectedClass} row {row}", observed,
                    !baseClean ? "not evaluable" : ok ? "yes" : "no",
                    $"base {baseEntry?.Run} character failures {(baseEntry is null ? "missing" : baseEntry.CharacterFailures.Count.ToString(CultureInfo.InvariantCulture))}; mutated row's pages: {geometry}"));
            }
        }
    }

    private static void ProbeItems(string path, List<ControlItem> items)
    {
        if (!File.Exists(path))
        {
            items.Add(new ControlItem("probe", "known answers", "-", "hold", [], "no", "no probe file"));
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var pdf in node["pdfs"]!.AsArray())
        {
            items.Add(new ControlItem("probe", pdf!["pdf"]!.GetValue<string>(), pdf["pdf"]!.GetValue<string>(), pdf["knownAnswer"]!.GetValue<string>(),
                [pdf["observedKnown"]!.GetValue<string>()], pdf["holds"]!.GetValue<bool>() ? "yes" : "no"));
        }
    }

    private static List<string> Observed(IntegrityEntry? ti) =>
        ti is null ? ["missing"] : [.. (ti.Failures ?? []).Take(5).Select(f => $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Region is null ? string.Empty : $" {f.Region}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}")];
}
