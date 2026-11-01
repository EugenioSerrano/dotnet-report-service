using System.Globalization;
using System.Text.Json.Nodes;
using GroupContinuityCheck;
using PageAccountingCheck;
using PaginationFixtures;
using PdfLayoutSpike;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupRulesCheck;

/// <summary>One control: what was expected, what was observed, and whether it behaved (yes, no, not evaluable).</summary>
internal sealed record ControlItem(string Group, string Id, string Run, string Expected, List<string> Observed, string Behaved, string? Note = null);

/// <summary>
/// The "controls" command. A control behaves only when its observed failures are
/// exactly its expected set — and a control expecting none shows none. Copied from
/// spikes/12-group-continuity (its run ids changed, its accounting and group entries
/// read from this experiment's evaluation): the text oracle; the strict accounting
/// check (positive, page-level, and the extraction-level faults AX-1 … AX-4); the
/// character check's agreement with the text verdict; the group check (positive, and
/// one in-memory fault per rule, GX-1 … GX-5). New here: the reading rule
/// (RH-1 … RH-6) and the behaviour observations (OX-1 … OX-6), with known answers
/// taken from that folder's committed groups.json. The instrument is trusted only
/// when every group behaves.
/// </summary>
internal static class Controls
{
    public const string AxBase = "s1-none-G200-split-t1.00-f0.90-on-r1";
    public const string GxBase = "s1-none-B1000-split-t1.00-f0.90-on-r1";
    public const string B100Base = "s1-none-B100-split-t1.00-f0.90-on-r1";
    public const string K1Base = "s1-none-K1-split-t1.00-f0.90-on-r1";
    public const string ExtractionBase = "s1-none-G200-split-t1.00-f0.90-off-r1";
    public const string Ge52 = "s1-none-GE52-split-t1.00-f0.90-on-r1";
    public const string Ge61 = "s1-none-GE61-split-t1.00-f0.90-on-r1";

    private static readonly string[] Groups =
        ["text", "strict", "faults", "character-agreement", "group-positive", "group-faults", "reading-rule", "observations"];

    public static async Task<int> RunAsync(string fixturesDirectory, string runsDirectory, string evaluationPath, string integrityRawPath, string extractionPath,
        string referenceGroupsPath, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var raw = Files.ReadIntegrity(integrityRawPath);
        var evaluation = EvaluateCommand.Read(evaluationPath).ToDictionary(e => e.Run, StringComparer.Ordinal);
        var accounting = evaluation.Values.Select(e => e.Accounting).ToList();
        var stage = StageFolder.Load(runsDirectory);
        var items = new List<ControlItem>();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        TextControls(raw, items);
        ExtractionTextControls(extractionPath, items);
        StrictControls(accounting, items);
        items.AddRange(await FaultControlsAsync(accounting, stage, fixturesDirectory, sums, timeout, cancellation));
        CharacterAgreement(evaluation, items);
        GroupPositives(evaluation, items);
        items.AddRange(await GroupFaultsAsync(accounting, stage, fixturesDirectory, sums, timeout, cancellation));
        ReadingRule(evaluation, raw, items);
        items.AddRange(await PlantedControlsAsync(evaluation, stage, fixturesDirectory, sums, GxBase, B100Base, timeout, cancellation));
        KnownAnswers(evaluation, referenceGroupsPath, items);

        var trusted = items.All(i => i.Behaved != "no") && Groups.All(g => items.Any(i => i.Group == g && i.Behaved == "yes"));
        foreach (var i in items)
        {
            Console.WriteLine($"{i.Group} {i.Id} on {i.Run}: {i.Behaved} — expected [{i.Expected}], observed [{string.Join(" | ", i.Observed)}]{(i.Note is null ? string.Empty : $" ({i.Note})")}");
        }

        Console.WriteLine($"Instrument trusted: {trusted}.");
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "controls.json"), new { trusted, items });
        return 0;
    }

    /// <summary>
    /// The "planted" command: RH-3 … RH-6, OX-4 and OX-5 alone, on the runs without behaviours of the
    /// quick loop (prefix "q-") or the stage (prefix "s1-") — the development loop runs it before the window.
    /// </summary>
    public static async Task<int> PlantedAsync(string fixturesDirectory, string runsDirectory, string evaluationPath, string prefix, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var evaluation = EvaluateCommand.Read(evaluationPath).ToDictionary(e => e.Run, StringComparer.Ordinal);
        var stage = StageFolder.Load(runsDirectory);
        var items = await PlantedControlsAsync(evaluation, stage, fixturesDirectory, sums, prefix + GxBase["s1-".Length..], prefix + B100Base["s1-".Length..],
            TimeSpan.FromSeconds(timeoutSeconds), cancellation);
        foreach (var i in items)
        {
            Console.WriteLine($"{i.Group} {i.Id} on {i.Run}: {i.Behaved} — expected [{i.Expected}], observed [{string.Join(" | ", i.Observed)}]{(i.Note is null ? string.Empty : $" ({i.Note})")}");
        }

        var all = items.All(i => i.Behaved == "yes");
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "planted.json"), new { behaved = all, items });
        return all ? 0 : 1;
    }

    private static StrictEntry? Run(List<StrictEntry> accounting, string id) => accounting.FirstOrDefault(e => e.Run == id);

    private static void TextControls(Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        foreach (var id in new[] { B100Base, K1Base })
        {
            var ti = integrity.GetValueOrDefault(id);
            items.Add(new ControlItem("text", "positive", id, "pass", Observed(ti), ti?.Verdict == "pass" ? "yes" : "no"));
        }

        foreach (var (mutation, fixture) in new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") })
        {
            var id = $"s1-none-{mutation}-{fixture}-split-t1.00-f0.90-on-r1";
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
        foreach (var id in new[] { B100Base, K1Base })
        {
            var e = Run(accounting, id);
            var failures = e?.FailureSet ?? ["missing"];
            items.Add(new ControlItem("strict", "positive", id, "-", failures, e is { Verdict: "checked" } && failures.Count == 0 ? "yes" : "no"));
        }

        foreach (var (mutation, fixture) in new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") })
        {
            var id = $"s1-none-{mutation}-{fixture}-split-t1.00-f0.90-on-r1";
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

    /// <summary>The character check passes a row exactly when the text verdict of record has no TI-2, TI-3 or TI-4 failure on it.</summary>
    private static void CharacterAgreement(Dictionary<string, EvaluatedEntry> evaluation, List<ControlItem> items)
    {
        var compared = 0;
        foreach (var ev in evaluation.Values.Where(x => x.Verdict == "checked"))
        {
            var e = ev.Accounting;
            var ti = ev.Text;
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

    private static void GroupPositives(Dictionary<string, EvaluatedEntry> evaluation, List<ControlItem> items)
    {
        foreach (var id in new[] { B100Base, K1Base, GxBase })
        {
            var g = evaluation.GetValueOrDefault(id);
            var observed = g?.Groups?.Failures.Select(f => f.Key).ToList() ?? ["missing"];
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

    /// <summary>
    /// RH-1: nothing removed in the configurations that print no repeated header.
    /// RH-2: where nothing is removed, the text verdict of record equals the text oracle's
    /// own image on the unfiltered PDF (verdict, failures, rules, content hash, words per page).
    /// </summary>
    private static void ReadingRule(Dictionary<string, EvaluatedEntry> evaluation, Dictionary<string, IntegrityEntry> raw, List<ControlItem> items)
    {
        const string group = "reading-rule";
        var nothingPrinted = evaluation.Values.Where(e => e.Verdict == "checked"
            && e.Configuration is Configurations.None or Configurations.KeepHeader or Configurations.KeepTotal).ToList();
        var removedThere = nothingPrinted.Where(e => e.Removed.Count > 0).Select(e => $"{e.Run}: {e.Removed.Count} removed").ToList();
        items.Add(new ControlItem(group, "RH-1", $"{nothingPrinted.Count} runs", "0 lines removed in every run", removedThere,
            nothingPrinted.Count > 0 && removedThere.Count == 0 ? "yes" : "no"));

        var compared = 0;
        var different = new List<string>();
        foreach (var e in evaluation.Values.Where(e => e.Verdict == "checked" && e.Removed.Count == 0))
        {
            var mine = e.Text;
            var theirs = raw.GetValueOrDefault(e.Run);
            compared++;
            if (mine is null || theirs is null)
            {
                different.Add($"{e.Run}: an entry is missing");
                continue;
            }

            var fields = new List<string>();
            if (mine.Verdict != theirs.Verdict)
            {
                fields.Add("verdict");
            }

            if (mine.FailureCount != theirs.FailureCount || FailureText(mine) != FailureText(theirs))
            {
                fields.Add("failures");
            }

            if (RuleText(mine) != RuleText(theirs))
            {
                fields.Add("rules");
            }

            if (mine.ContentSha256 != theirs.ContentSha256)
            {
                fields.Add("contentSha256");
            }

            if (!(mine.PageWordCounts ?? []).SequenceEqual(theirs.PageWordCounts ?? []))
            {
                fields.Add("pageWordCounts");
            }

            if (fields.Count > 0)
            {
                different.Add($"{e.Run}: {string.Join(", ", fields)}");
            }
        }

        items.Add(new ControlItem(group, "RH-2", $"{compared} runs", "equal to the unfiltered text oracle on every run with nothing removed", different,
            compared > 0 && different.Count == 0 ? "yes" : "no"));

        static string RuleText(IntegrityEntry e) => e.Rules is null ? string.Empty : string.Join(",", e.Rules.Select(kv => $"{kv.Key}={kv.Value}"));

        static string FailureText(IntegrityEntry e) => string.Join("\n", (e.Failures ?? []).Select(f => $"{f.Rule}|{f.Class}|{f.Row}|{f.Region}|{f.Field}|{f.Page}|{f.Detail}"));
    }

    /// <summary>RH-3 … RH-6, OX-4 and OX-5: lines planted in memory on a run without behaviours, then the reading rule, the unchanged checks and the observations.</summary>
    private static async Task<List<ControlItem>> PlantedControlsAsync(Dictionary<string, EvaluatedEntry> evaluation, StageFolder stage, string fixturesDirectory,
        Dictionary<string, string> sums, string b1000Base, string b100Base, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = new List<ControlItem>();
        var b1000 = await BaseAsync(b1000Base);
        var b100 = await BaseAsync(b100Base);
        var gs1200 = FixtureFile.ReadVerified(fixturesDirectory, "GS1200", sums);
        if (b1000 is null || b100 is null)
        {
            foreach (var id in new[] { "RH-3", "RH-4", "RH-5", "RH-6", "OX-4", "OX-5" })
            {
                result.Add(new ControlItem(id.StartsWith("RH", StringComparison.Ordinal) ? "reading-rule" : "observations", id, b1000 is null ? b1000Base : b100Base, "-", [], "no", "the base is missing"));
            }

            return result;
        }

        var (doc, fixture, run) = b1000.Value;
        var baseRead = EvaluateCommand.Read(doc, fixture, true);
        var baseEntry = Judge(b1000Base, baseRead, fixture, run);
        var g2 = SourceGroups.Of(fixture).First(g => g.Id == "G0002");
        var headerAt = doc.ContentLines.FindIndex(l => l.Words.Count > 0 && l.Words[0].Text == "G0002");
        var headerPage = doc.ContentLines[headerAt].Page;
        var targetPage = baseRead.Pdf.RowPages.Where(kv => g2.Items.Any(i => i.Sequence == kv.Key)).SelectMany(kv => kv.Value).Where(p => p > headerPage).DefaultIfEmpty(0).Min();
        var targetAt = doc.ContentLines.FindIndex(l => l.Page == targetPage);
        var header = doc.ContentLines[headerAt];

        // RH-3: the exact form is removed, and only it; the reading equals the base's.
        result.Add(Planted("reading-rule", "RH-3", b1000Base, doc, fixture, run, targetAt, Planted(header, targetPage, g2.Caption + RepeatedHeaders.Suffix),
            "1 line removed (G0002 p" + targetPage + "); text result and row sets equal to the base's", (r, entry) =>
        {
            var ok = r.Reading.Removed.Count == 1 && r.Reading.Removed[0].Group == "G0002" && r.Reading.Removed[0].Page == targetPage
                && entry.Text!.ContentSha256 == baseEntry.Text!.ContentSha256 && entry.Text.FailureCount == baseEntry.Text.FailureCount
                && entry.Accounting.RowSetsSha256 == baseEntry.Accounting.RowSetsSha256;
            return (ok, [$"removed {r.Reading.Removed.Count}", $"text failures {entry.Text!.FailureCount}", $"row sets equal {entry.Accounting.RowSetsSha256 == baseEntry.Accounting.RowSetsSha256}"]);
        }));

        // RH-4: one character of the group name changed — not removed, and the unchanged checks see it.
        var altered = Alter(g2.Caption);
        result.Add(Planted("reading-rule", "RH-4", b1000Base, doc, fixture, run, targetAt, Planted(header, targetPage, altered + RepeatedHeaders.Suffix),
            "0 removed; exactly TI-5 and GC-1|header-text|G0002; no rule 1-6", (r, entry) => Sole(r, entry, ["GC-1|header-text|G0002"])));

        // RH-5: another group's caption (G0007, held by GS1200, not by B1000) — not removed, and the unchanged checks see it.
        var g7 = SourceGroups.Of(gs1200).First(g => g.Id == "G0007");
        result.Add(Planted("reading-rule", "RH-5", b1000Base, doc, fixture, run, targetAt, Planted(header, targetPage, g7.Caption + RepeatedHeaders.Suffix),
            "0 removed; exactly TI-5 and GC-1|header-unknown|G0007; no rule 1-6", (r, entry) => Sole(r, entry, ["GC-1|header-unknown|G0007"])));

        // RH-6: the row mark's text instead of "(cont.)" — never matched.
        result.Add(Planted("reading-rule", "RH-6", b1000Base, doc, fixture, run, targetAt, Planted(header, targetPage, g2.Caption + " " + PdfReading.Continues),
            "0 removed (a rule-level assertion)", (r, entry) => (r.Reading.Removed.Count == 0, [$"removed {r.Reading.Removed.Count}"])));

        // OX-4: the exact form on the group's own header page, before its header — removed and counted as spurious.
        result.Add(Planted("observations", "OX-4", b1000Base, doc, fixture, run, headerAt, Planted(header, headerPage, g2.Caption + RepeatedHeaders.Suffix),
            $"removed 1; spurious exactly G0002 p{headerPage}; reading equal to the base's", (r, entry) =>
        {
            var spurious = entry.Behaviours!.RepeatHeader.Spurious;
            var ok = r.Reading.Removed.Count == 1 && spurious.Count == 1 && spurious[0] == $"G0002 p{headerPage}"
                && entry.Accounting.RowSetsSha256 == baseEntry.Accounting.RowSetsSha256 && entry.Text!.ContentSha256 == baseEntry.Text!.ContentSha256;
            return (ok, [$"removed {r.Reading.Removed.Count}", $"spurious {string.Join(",", spurious)}"]);
        }));

        // OX-5: the exact form at the top of B100's pages 2, 3 and 4 — three instances, all produced.
        var (doc100, fixture100, run100) = b100.Value;
        var base100 = Judge(b100Base, EvaluateCommand.Read(doc100, fixture100, true), fixture100, run100);
        var g1 = SourceGroups.Of(fixture100).First(g => g.Id == "G0001");
        var header100 = doc100.ContentLines.First(l => l.Words.Count > 0 && l.Words[0].Text == "G0001");
        var copy100 = doc100.Clone();
        foreach (var page in new[] { 4, 3, 2 })
        {
            var at = copy100.ContentLines.FindIndex(l => l.Page == page);
            if (at >= 0)
            {
                copy100.ContentLines.Insert(at, Planted(header100, page, g1.Caption + RepeatedHeaders.Suffix));
            }
        }

        var read100 = EvaluateCommand.Read(copy100, fixture100, true);
        var entry100 = Judge(b100Base + "+OX-5", read100, fixture100, run100);
        var b2 = entry100.Behaviours!.RepeatHeader;
        var ox5 = b2.Instances == 3 && b2.Produced == 3 && b2.Violations.Count == 0 && b2.Spurious.Count == 0
            && entry100.Accounting.RowSetsSha256 == base100.Accounting.RowSetsSha256 && entry100.Text!.ContentSha256 == base100.Text!.ContentSha256;
        result.Add(new ControlItem("observations", "OX-5", b100Base, "instances 3, produced 3, violations 0, spurious 0; reading equal to the base's",
            [$"instances {b2.Instances}", $"produced {b2.Produced}", $"violations {string.Join(",", b2.Violations)}", $"spurious {string.Join(",", b2.Spurious)}"], ox5 ? "yes" : "no"));
        return result;

        async Task<(PrintedDocument Doc, Fixture Fixture, HarnessRun Run)?> BaseAsync(string id)
        {
            var e = evaluation.GetValueOrDefault(id);
            if (e is not { Verdict: "checked" } || !stage.Runs.TryGetValue(id, out var r) || r.Pdf is null)
            {
                return null;
            }

            var f = FixtureFile.ReadVerified(fixturesDirectory, e.FixtureId, sums);
            var d = PrintedDocument.From(await Poppler.ExtractAsync(Path.Combine(stage.Directory, r.Pdf), timeout, cancellation));
            return (d, f, r);
        }
    }

    /// <summary>The checks and observations of one planted document, as "evaluate" computes them for a run.</summary>
    private static EvaluatedEntry Judge(string id, (ReadingResult Reading, PdfReading Pdf, IntegrityResult Text) read, Fixture fixture, HarnessRun run)
    {
        var entry = new EvaluatedEntry { Run = id, FixtureId = fixture.Id, FooterOn = true, Repetition = 1, Removed = read.Reading.Removed };
        entry.Text = EvaluateCommand.TextEntry(id, null, read.Text);
        entry.Accounting = new StrictEntry { Run = id, Markup = "grid", FixtureId = fixture.Id, FooterOn = true, Repetition = 1 };
        Account.Judge(entry.Accounting, read.Pdf, fixture, run, entry.Text);
        entry.Groups = GroupCheck.Check(read.Pdf, fixture);
        entry.Behaviours = BehaviourObservations.Observe(read.Reading, read.Pdf, entry.Groups, fixture);
        entry.Verdict = "checked";
        return entry;
    }

    private static ControlItem Planted(string group, string id, string baseRun, PrintedDocument original, Fixture fixture, HarnessRun run, int insertAt, Line line,
        string expected, Func<(ReadingResult Reading, PdfReading Pdf, IntegrityResult Text), EvaluatedEntry, (bool Ok, List<string> Observed)> judge)
    {
        if (insertAt < 0)
        {
            return new ControlItem(group, id, baseRun, expected, [], "no", "the planted line has no place in the base");
        }

        var copy = original.Clone();
        copy.ContentLines.Insert(insertAt, line);
        var read = EvaluateCommand.Read(copy, fixture, true);
        var entry = Judge(baseRun + "+" + id, read, fixture, run);
        var (ok, observed) = judge(read, entry);
        return new ControlItem(group, id, baseRun, expected, observed, ok ? "yes" : "no", $"planted '{line.Text}' on page {line.Page}");
    }

    /// <summary>The sole-failure rule for RH-4 and RH-5: nothing removed, text failures in TI-5 only, the group check exactly the expected keys, no rule 1-6.</summary>
    private static (bool Ok, List<string> Observed) Sole((ReadingResult Reading, PdfReading Pdf, IntegrityResult Text) r, EvaluatedEntry entry, List<string> expectedGroup)
    {
        var tiRules = r.Text.Failures.Select(f => f.Rule).Distinct().Order(StringComparer.Ordinal).ToList();
        var gc = entry.Groups!.Failures.Select(f => f.Key).Distinct().ToList();
        var strict = entry.Accounting.FailureSet.Where(f => Account.RuleNumber(f.Split('|')[0]) <= 6).ToList();
        var ok = r.Reading.Removed.Count == 0 && tiRules.SequenceEqual(["TI-5"]) && gc.Count == expectedGroup.Count && expectedGroup.All(gc.Contains) && strict.Count == 0;
        return (ok, [$"removed {r.Reading.Removed.Count}", $"text rules {string.Join(",", tiRules)}", $"group {string.Join(",", gc)}", $"rules 1-6 {string.Join(",", strict)}"]);
    }

    /// <summary>A line made from a header line's words: the same horizontal places, moved to a page, its words replaced by the given text (one word per space).</summary>
    private static Line Planted(Line header, int page, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = header.Words[0];
        var height = first.YMax - first.YMin;
        var y = PrintedDocument.TopPt + 1;
        var list = new List<PdfWord>();
        var x = first.XMin;
        for (var i = 0; i < words.Length; i++)
        {
            var width = i < header.Words.Count ? header.Words[i].XMax - header.Words[i].XMin : 6 * words[i].Length;
            var xMin = i < header.Words.Count ? header.Words[i].XMin : x;
            list.Add(new PdfWord(page, xMin, y, xMin + width, y + height, words[i]));
            x = xMin + width + 3;
        }

        return new Line { Page = page, Words = list };
    }

    /// <summary>The caption with the first letter of the group name changed (a one-character alteration).</summary>
    private static string Alter(string caption)
    {
        var at = caption.IndexOf('—', StringComparison.Ordinal);
        var i = at < 0 ? caption.Length - 1 : at + 2;
        var c = caption[i];
        var replaced = c == 'X' ? 'Y' : 'X';
        return caption[..i] + replaced + caption[(i + 1)..];
    }

    /// <summary>OX-1 … OX-3 and OX-6: the observations on runs without behaviours against the known answers of spikes/12-group-continuity's committed groups.json.</summary>
    private static void KnownAnswers(Dictionary<string, EvaluatedEntry> evaluation, string referenceGroupsPath, List<ControlItem> items)
    {
        const string group = "observations";
        var reference = JsonNode.Parse(File.ReadAllText(referenceGroupsPath))!["runs"]!.AsArray()
            .ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!["result"], StringComparer.Ordinal);

        // OX-1: GE52 — the header left alone, exactly G0004 on page 1 (the reference lists the same).
        Check("OX-1", Ge52, "s1-grid-new-GE52-split-t1.00-f0.90-on-r1", "header alone exactly G0004 p1", (b, refGroups) =>
        {
            var refAlone = refGroups.Where(g => g!["headerAlonePages"]!.AsArray().Count > 0).Select(g => $"{g!["group"]} alone p{string.Join(",p", g["headerAlonePages"]!.AsArray().Select(p => p!.GetValue<int>()))}").ToList();
            return (b.KeepHeader.Violations.SequenceEqual(["G0004 alone p1"]) && refAlone.SequenceEqual(["G0004 alone p1"]), b.KeepHeader.Violations);
        });

        // OX-2: B100 — the open group G0001 continues on pages 2, 3 and 4 without a repeated header.
        Check("OX-2", B100Base, "s1-grid-s1-B100-split-t1.00-f0.90-on-r1", "repeated header missing exactly on G0001 p2, p3, p4; no spurious line", (b, refGroups) =>
        {
            var refPages = refGroups.Where(g => g!["group"]!.GetValue<string>() == "G0001").SelectMany(g => g!["itemPagesWithoutHeader"]!.AsArray().Select(p => p!.GetValue<int>())).ToList();
            var ok = b.RepeatHeader.ViolatedInstances.SequenceEqual(["G0001 p2", "G0001 p3", "G0001 p4"]) && b.RepeatHeader.Violations.All(v => v.EndsWith(": missing", StringComparison.Ordinal))
                && b.RepeatHeader.Spurious.Count == 0 && refPages.SequenceEqual([2, 3, 4]);
            return (ok, [.. b.RepeatHeader.Violations, .. b.RepeatHeader.Spurious.Select(s => "spurious " + s)]);
        });

        // OX-3: GE61 — G0003's total on page 2, its row on page 1.
        Check("OX-3", Ge61, "s1-grid-new-GE61-split-t1.00-f0.90-on-r1", "total separated exactly G0003 total p2, last row p1", (b, refGroups) =>
        {
            var g3 = refGroups.FirstOrDefault(g => g!["group"]!.GetValue<string>() == "G0003");
            var refOk = g3 is not null && g3["totalPage"]?.GetValue<int>() == 2 && g3["lastItemPage"]?.GetValue<int>() == 1;
            return (b.KeepTotal.Violations.SequenceEqual(["G0003 total p2, last row p1"]) && refOk, b.KeepTotal.Violations);
        });

        // OX-6: positives — B100: no header alone, no closed group; K1: no violation and no spurious line.
        var b100 = evaluation.GetValueOrDefault(B100Base)?.Behaviours;
        var k1 = evaluation.GetValueOrDefault(K1Base)?.Behaviours;
        var ox6 = b100 is not null && k1 is not null && b100.KeepHeader.Violations.Count == 0 && b100.KeepTotal.Instances == 0
            && new[] { k1.KeepHeader, k1.RepeatHeader, k1.KeepTotal }.All(c => c.Violations.Count == 0 && c.Spurious.Count == 0);
        items.Add(new ControlItem(group, "OX-6", $"{B100Base}, {K1Base}", "B100: header violations 0, total instances 0; K1: violations 0, spurious 0",
            [$"B100 header violations {b100?.KeepHeader.Violations.Count}", $"B100 total instances {b100?.KeepTotal.Instances}",
                $"K1 violations {(k1 is null ? "-" : (k1.KeepHeader.Violations.Count + k1.RepeatHeader.Violations.Count + k1.KeepTotal.Violations.Count).ToString(CultureInfo.InvariantCulture))}"],
            ox6 ? "yes" : "no"));

        void Check(string id, string run, string referenceRun, string expected, Func<BehaviourResult, JsonArray, (bool Ok, List<string> Observed)> judge)
        {
            var b = evaluation.GetValueOrDefault(run)?.Behaviours;
            var refGroups = reference.GetValueOrDefault(referenceRun)?["groups"]?.AsArray();
            if (b is null || refGroups is null)
            {
                items.Add(new ControlItem(group, id, run, expected, [], "no", b is null ? "the run has no observation" : "the reference has no such run"));
                return;
            }

            var (ok, observed) = judge(b, refGroups);
            items.Add(new ControlItem(group, id, run, expected, observed, ok ? "yes" : "no", $"known answer from {referenceRun}"));
        }
    }

    private static List<string> Observed(IntegrityEntry? ti) =>
        ti is null ? ["missing"] : [.. (ti.Failures ?? []).Take(5).Select(f => $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Region is null ? string.Empty : $" {f.Region}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}")];
}
