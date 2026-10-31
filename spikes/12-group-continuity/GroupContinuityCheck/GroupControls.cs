using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using PdfLayoutSpike;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>One control: what was expected, what was observed, and whether it behaved (yes, no, not evaluable).</summary>
internal sealed record ControlItem(string Group, string Id, string Run, string Expected, List<string> Observed, string Behaved, string? Note = null);

/// <summary>
/// The "controls" command. A control behaves only when its observed failures are
/// exactly its expected set — and a control expecting none shows none. Groups: the
/// text oracle in grid; the strict accounting check (positive, page-level, and the
/// extraction-level faults AX-1 … AX-4, whose code is that of
/// spikes/10-pagedjs-table-strict-accounting); the character check's agreement with
/// the text oracle; the group check (positive, and one in-memory fault per rule,
/// GX-1 … GX-5); the probe. The instrument is trusted only when every group behaves.
/// </summary>
internal static class GroupControls
{
    public const string AxBase = "s1-grid-s2-G200-split-t1.00-f0.90-on-r1";
    public const string GxBase = "s1-grid-s1-B1000-split-t1.00-f0.90-on-r1";
    public const string ExtractionBase = "s1-grid-s2-G200-split-t1.00-f0.90-off-r1";

    public static async Task<int> RunAsync(string fixturesDirectory, string runsDirectory, string integrityPath, string accountingPath, string groupsPath,
        string extractionPath, string probePath, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var integrity = Files.ReadIntegrity(integrityPath);
        var accounting = ReadAccounting(accountingPath);
        var groups = GroupsCommand.Read(groupsPath).ToDictionary(g => g.Run, StringComparer.Ordinal);
        var stage = StageFolder.Load(runsDirectory);
        var items = new List<ControlItem>();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        TextControls(accounting, integrity, items);
        ExtractionTextControls(extractionPath, items);
        StrictControls(accounting, items);
        items.AddRange(await FaultControlsAsync(accounting, stage, fixturesDirectory, sums, timeout, cancellation));
        CharacterAgreement(accounting, integrity, items);
        GroupPositives(groups, items);
        items.AddRange(await GroupFaultsAsync(accounting, stage, fixturesDirectory, sums, timeout, cancellation));
        ProbeItems(probePath, items);

        var trusted = items.All(i => i.Behaved != "no") && new[] { "text", "strict", "faults", "character-agreement", "group-positive", "group-faults", "probe" }
            .All(g => items.Any(i => i.Group == g && i.Behaved == "yes"));
        foreach (var i in items)
        {
            Console.WriteLine($"{i.Group} {i.Id} on {i.Run}: {i.Behaved} — expected [{i.Expected}], observed [{string.Join(" | ", i.Observed)}]{(i.Note is null ? string.Empty : $" ({i.Note})")}");
        }

        Console.WriteLine($"Instrument trusted: {trusted}.");
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "controls.json"), new { trusted, items });
        return 0;
    }

    public static List<StrictEntry> ReadAccounting(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["runs"]!.Deserialize<List<StrictEntry>>(StageFolder.Json) ?? [];

    private static StrictEntry? Run(List<StrictEntry> accounting, string id) => accounting.FirstOrDefault(e => e.Run == id);

    private static void TextControls(List<StrictEntry> accounting, Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        foreach (var id in new[] { "s1-grid-s1-B100-split-t1.00-f0.90-on-r1", "s1-grid-s1-K1-split-t1.00-f0.90-on-r1" })
        {
            var ti = integrity.GetValueOrDefault(id);
            items.Add(new ControlItem("text", "positive", id, "pass", Observed(ti), ti?.Verdict == "pass" ? "yes" : "no"));
        }

        foreach (var (mutation, fixture, baseId) in new[]
        {
            ("NC-W", "B100", "s1-grid-s1-B100-split-t1.00-f0.90-on-r1"),
            ("NC-L", "B100", "s1-grid-s1-B100-split-t1.00-f0.90-on-r1"),
            ("NC-R", "B100", "s1-grid-s1-B100-split-t1.00-f0.90-on-r1"),
            ("NC-F", "F26K-a", "s1-grid-s1-B100-split-t1.00-f0.90-on-r1"),
        })
        {
            var id = $"s1-grid-s1-{mutation}-{fixture}-split-t1.00-f0.90-on-r1";
            var ti = integrity.GetValueOrDefault(id);
            var (cls, row, region) = ControlMutations.Expected(mutation);
            var expected = $"{cls}{(row is null ? string.Empty : $" row {row}")}{(region is null ? string.Empty : $" {region}")}";
            var sole = ti is not null && ti.FailureCount == 1 && ti.Failures is [var only] && only.Class == cls && (row is null || only.Row == row) && (region is null || only.Region == region);
            var any = ti?.Failures?.Any(f => f.Class == cls && (row is null || f.Row == row) && (region is null || f.Region == region)) == true;
            items.Add(new ControlItem("text", mutation, id, expected, Observed(ti), sole ? "yes" : "no", $"any failure matches: {any}"));
        }
    }

    /// <summary>E1-E4 of the text oracle's own controls command, by the sole-failure rule; E5-E8 not evaluable.</summary>
    private static void ExtractionTextControls(string path, List<ControlItem> items)
    {
        if (!File.Exists(path))
        {
            items.Add(new ControlItem("text", "E1-E4", ExtractionBase, "each its one failure", [], "no", "no extraction-control file"));
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
                items.Add(new ControlItem("text", id, baseRun ?? "-", expected, observed, "not evaluable",
                    $"the text oracle's controls cannot take real marks (they add simulated marks and evaluate without the footer); its own result: {behaved}"));
                continue;
            }

            var ok = baseRun == ExtractionBase && behaved == "yes" && observed.Count == 1;
            items.Add(new ControlItem("text", id, baseRun ?? "-", expected, observed, ok ? "yes" : "no", $"its own result: {behaved}"));
        }
    }

    private static void StrictControls(List<StrictEntry> accounting, List<ControlItem> items)
    {
        foreach (var id in new[] { "s1-grid-s1-B100-split-t1.00-f0.90-on-r1", "s1-grid-s1-K1-split-t1.00-f0.90-on-r1" })
        {
            var e = Run(accounting, id);
            var failures = e?.FailureSet ?? ["missing"];
            items.Add(new ControlItem("strict", "positive", id, "-", failures, e is { Verdict: "checked" } && failures.Count == 0 ? "yes" : "no"));
        }

        foreach (var (mutation, fixture) in new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") })
        {
            var id = $"s1-grid-s1-{mutation}-{fixture}-split-t1.00-f0.90-on-r1";
            var e = Run(accounting, id);
            var observed = e?.FailureSet ?? ["missing"];
            bool ok;
            string expected;
            if (mutation == "NC-R")
            {
                expected = $"{AccountingVerifier.Rows}|p (sequence 51 missing); {AccountingVerifier.Chain}|p<last page>";
                var rule1 = e?.Rules.FirstOrDefault(r => r.Name == AccountingVerifier.Rows);
                ok = e is { Verdict: "checked" } && observed.Count == 2 && observed.Contains($"{AccountingVerifier.Rows}|p")
                    && rule1?.Detail == "1 sequences missing, first 51" && observed.Contains($"{AccountingVerifier.Chain}|p{e.Pages}");
            }
            else
            {
                expected = "-";
                ok = e is { Verdict: "checked" } && observed.Count == 0;
            }

            items.Add(new ControlItem("strict", mutation, id, expected, observed, ok ? "yes" : "no"));
        }
    }

    /// <summary>AX-1 … AX-4 on the named base (the code of spikes/10-pagedjs-table-strict-accounting, unchanged in its logic).</summary>
    private static async Task<List<ControlItem>> FaultControlsAsync(List<StrictEntry> accounting, StageFolder stage, string fixturesDirectory, Dictionary<string, string> sums, TimeSpan timeout, CancellationToken cancellation)
    {
        const string group = "faults";
        var result = new List<ControlItem>();
        var baseEntry = Run(accounting, AxBase);
        if (baseEntry is not { Verdict: "checked" } || baseEntry.FailureSet.Any(f => Account.RuleNumber(f.Split('|')[0]) <= 6))
        {
            foreach (var id in new[] { "AX-1", "AX-2", "AX-3", "AX-4" })
            {
                result.Add(new ControlItem(group, id, AxBase, "-", [], "no", "the base is missing or fails a rule 1-6"));
            }

            return result;
        }

        var run = stage.Runs[baseEntry.Run];
        var fixtureFile = FixtureFile.ReadVerified(fixturesDirectory, baseEntry.FixtureId, sums);
        var text = await Poppler.ExtractAsync(Path.Combine(stage.Directory, run.Pdf!), timeout, cancellation);
        var original = PrintedDocument.From(text);
        var reading = PdfReading.From(original, fixtureFile, "expected", "accounting");
        var rows = AccountingVerifier.Rows;
        var subtotals = AccountingVerifier.EndPageSubtotals;
        var chain = AccountingVerifier.Chain;
        var marks = AccountingVerifier.Marks;

        result.Add(Fault(group, "AX-1", baseEntry, fixtureFile, original, run, [$"{subtotals}|p2", $"{chain}|p2"], d => ShiftFooterValue(d, 2, "subtotal:")));
        result.Add(Fault(group, "AX-2", baseEntry, fixtureFile, original, run, [$"{chain}|p3"], d => ShiftFooterValue(d, 3, "forward:", first: true)));
        var split = reading.RowPages.Where(kv => kv.Value.Max > kv.Value.Min).OrderBy(kv => kv.Key).Select(kv => (Row: kv.Key, Page: kv.Value.Min)).FirstOrDefault();
        if (split.Row == 0)
        {
            result.Add(new ControlItem(group, "AX-3", baseEntry.Run, "-", [], "no", "the base has no split row"));
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

    /// <summary>The character check passes a row exactly when the text oracle has no TI-2, TI-3 or TI-4 failure on it.</summary>
    private static void CharacterAgreement(List<StrictEntry> accounting, Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        var compared = 0;
        foreach (var e in accounting.Where(e => e.Verdict == "checked"))
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

        items.Add(new ControlItem("character-agreement", "summary", "grid", "agreement on every row", [$"{compared} runs compared"], compared > 0 ? "yes" : "no"));
    }

    private static void GroupPositives(Dictionary<string, GroupEntry> groups, List<ControlItem> items)
    {
        foreach (var id in new[] { "s1-grid-s1-B100-split-t1.00-f0.90-on-r1", "s1-grid-s1-K1-split-t1.00-f0.90-on-r1", GxBase })
        {
            var g = groups.GetValueOrDefault(id);
            var observed = g?.Result?.Failures.Select(f => f.Key).ToList() ?? ["missing"];
            items.Add(new ControlItem("group-positive", "positive", id, "-", observed, g is { Verdict: "checked" } && observed.Count == 0 ? "yes" : "no"));
        }
    }

    /// <summary>GX-1 … GX-5: one in-memory fault per group rule on the named base; each must fail exactly its set, and no accounting rule 1-6.</summary>
    private static async Task<List<ControlItem>> GroupFaultsAsync(List<StrictEntry> accounting, StageFolder stage, string fixturesDirectory, Dictionary<string, string> sums, TimeSpan timeout, CancellationToken cancellation)
    {
        const string group = "group-faults";
        var result = new List<ControlItem>();
        var baseEntry = Run(accounting, GxBase);
        if (baseEntry is not { Verdict: "checked" })
        {
            return [new ControlItem(group, "GX-1..GX-5", GxBase, "-", [], "no", "the base is missing")];
        }

        var run = stage.Runs[GxBase];
        var fixture = FixtureFile.ReadVerified(fixturesDirectory, baseEntry.FixtureId, sums);
        var text = await Poppler.ExtractAsync(Path.Combine(stage.Directory, run.Pdf!), timeout, cancellation);
        var original = PrintedDocument.From(text);
        var reading = PdfReading.From(original, fixture, "expected", "accounting");
        var baseResult = GroupCheck.Check(reading, fixture);
        if (baseResult.Failures.Count > 0)
        {
            return [new ControlItem(group, "GX-1..GX-5", GxBase, "-", [.. baseResult.Failures.Select(f => f.Key)], "no", "the base fails the group check")];
        }

        var source = SourceGroups.Of(fixture);
        var lines = original.ContentLines;
        int HeaderLine(string id) => lines.FindIndex(l => l.Words.Count > 0 && l.Words[0].Text == id);
        var totalLines = Enumerable.Range(0, lines.Count).Where(i => lines[i].Words.Count >= 2 && lines[i].Words[0].Text == "Group" && lines[i].Words[1].Text == "total").ToList();
        var closed = source.Where(g => g.Closed).ToList();
        var first = closed[0];
        var second = source[1];

        result.Add(GroupFault(group, "GX-1", GxBase, fixture, original, run, [$"GC-1|header-missing|{second.Id}"], d =>
        {
            var at = HeaderLine(second.Id);
            d.ContentLines.RemoveAt(at);
            return $"removed the header line of {second.Id} (line {at}, page {lines[at].Page})";
        }));

        result.Add(GroupFault(group, "GX-2", GxBase, fixture, original, run, [$"GC-2|total-wrong|{first.Id}"], d =>
        {
            var line = d.ContentLines[totalLines[0]];
            var word = line.Words[^1];
            var value = decimal.Parse(word.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            line.Words[^1] = word with { Text = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture) };
            return $"{first.Id} total {word.Text} -> {line.Words[^1].Text}";
        }));

        result.Add(GroupFault(group, "GX-3", GxBase, fixture, original, run, [$"GC-2|total-duplicated|{first.Id}"], d =>
        {
            var line = d.ContentLines[totalLines[0]];
            d.ContentLines.Insert(totalLines[0] + 1, new Line { Page = line.Page, Words = [.. line.Words] });
            return $"duplicated the total line of {first.Id} on page {line.Page}";
        }));

        // GX-4: the first closed group whose total and last row print on the same page.
        var candidate = closed.Select(g => (Group: g, Total: totalLines.FirstOrDefault(t => t > HeaderLine(g.Id)), Last: g.Items[^1].Sequence!.Value))
            .FirstOrDefault(c => c.Total > 0 && reading.AnchorLine.ContainsKey(c.Last) && lines[reading.AnchorLine[c.Last]].Page == lines[c.Total].Page);
        if (candidate.Group is null)
        {
            result.Add(new ControlItem(group, "GX-4", GxBase, "-", [], "no", "no closed group with its total and last row on one page"));
        }
        else
        {
            result.Add(GroupFault(group, "GX-4", GxBase, fixture, original, run, [$"GC-3|row-outside-group|{candidate.Group.Id}"], d =>
            {
                var total = d.ContentLines[candidate.Total];
                d.ContentLines.RemoveAt(candidate.Total);
                var anchor = reading.AnchorLine[candidate.Last];
                d.ContentLines.Insert(anchor, total);
                return $"moved the total line of {candidate.Group.Id} above its last row {candidate.Last} (page {total.Page})";
            }));
        }

        result.Add(GroupFault(group, "GX-5", GxBase, fixture, original, run, ["GC-4|block-total-wrong|-"], d =>
        {
            var at = d.ContentLines.FindIndex(l => l.Words.Count >= 2 && l.Words[0].Text == "Block" && l.Words[1].Text == "total");
            var line = d.ContentLines[at];
            var word = line.Words[^1];
            var value = decimal.Parse(word.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            line.Words[^1] = word with { Text = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture) };
            return $"block total {word.Text} -> {line.Words[^1].Text}";
        }));
        return result;
    }

    private static ControlItem GroupFault(string group, string id, string baseRun, Fixture fixture, PrintedDocument original, HarnessRun run, List<string> expected, Func<PrintedDocument, string> mutate)
    {
        var copy = original.Clone();
        string applied;
        try
        {
            applied = mutate(copy);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return new ControlItem(group, id, baseRun, string.Join("; ", expected), [], "no", "the fault could not be applied: " + e.Message);
        }

        var reading = PdfReading.From(copy, fixture, "expected", "accounting");
        var groupResult = GroupCheck.Check(reading, fixture);
        var entry = new StrictEntry { Run = baseRun + "+" + id, Markup = "grid", FooterOn = true, Repetition = 1 };
        Account.Judge(entry, reading, fixture, run, null);
        var observed = groupResult.Failures.Select(f => f.Key).Distinct().Concat(entry.FailureSet.Where(f => Account.RuleNumber(f.Split('|')[0]) <= 6)).ToList();
        var behaved = observed.Count == expected.Count && expected.All(observed.Contains) ? "yes" : "no";
        return new ControlItem(group, id, baseRun, string.Join("; ", expected), observed, behaved, applied);
    }

    private static void ProbeItems(string path, List<ControlItem> items)
    {
        if (!File.Exists(path))
        {
            items.Add(new ControlItem("probe", "known answers", "-", "hold", [], "no", "no probe file"));
            return;
        }

        foreach (var pdf in JsonNode.Parse(File.ReadAllText(path))!["pdfs"]!.AsArray())
        {
            items.Add(new ControlItem("probe", pdf!["pdf"]!.GetValue<string>(), pdf["pdf"]!.GetValue<string>(), pdf["knownAnswer"]!.GetValue<string>(),
                [pdf["observedKnown"]!.GetValue<string>()], pdf["holds"]!.GetValue<bool>() ? "yes" : "no"));
        }
    }

    private static List<string> Observed(IntegrityEntry? ti) =>
        ti is null ? ["missing"] : [.. (ti.Failures ?? []).Take(5).Select(f => $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Region is null ? string.Empty : $" {f.Region}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}")];
}
