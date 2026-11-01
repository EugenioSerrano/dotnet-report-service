using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>One control: what was expected, what was observed, and whether it behaved (yes, no, not evaluable).</summary>
internal sealed record ControlItem(string Group, string Id, string Run, string Expected, List<string> Observed, string Behaved, string? Note = null);

/// <summary>
/// The "controls" command. A control behaves only when its observed failures are
/// exactly its expected set — across the report accounting rules (XA), the group rules
/// (RG), the chain at every boundary (PB-7 broken boundaries), the numbering check and
/// the text oracle — and a control expecting none shows none. The instrument is
/// trusted only when every group of items behaves.
/// </summary>
internal static class ReportControls
{
    public const string AccountingBase = "XB-W250-H1-r1";
    public const string MarksBase = "XB-TL150-H1-r1";
    public const string NearEndBase = "XB-L180-H1-r1";
    public const string NearStartBase = "XB-L183-H1-r1";
    public const string NumberingBase = AccountingBase;
    public const string ExtractionBase = "s1-grid-s2-G200-split-t1.00-f0.90-off-r1";

    public static readonly string[] ParityRuns =
    [
        "s1-grid-s1-B1000-split-t1.00-f0.90-on-r1",
        "s1-grid-new-GE52-split-t1.00-f0.90-on-r1",
        "s1-grid-new-GE61-split-t1.00-f0.90-on-r1",
        "s1-grid-new-GS1200-split-t1.00-f0.90-on-r1",
        "s1-grid-new-GTF300u-split-t1.00-f0.90-on-r1",
        "s1-grid-new-GTL300u-split-t1.00-f0.90-on-r1",
    ];

    public static async Task<int> RunAsync(Dictionary<string, string> o, int timeoutSeconds, CancellationToken cancellation)
    {
        var outDirectory = o["out"];
        Directory.CreateDirectory(outDirectory);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var fixtures = o["fixtures"];
        var sums = Sums.Read(Path.Combine(fixtures, FixtureFile.SumsFileName));
        var stage = StageFolder.Load(o["runs"]);
        var integrity = Files.ReadIntegrity(o["integrity"]);
        var sliceIntegrity = Files.ReadIntegrity(o["integrity-slices"]);
        var accounting = (JsonNode.Parse(File.ReadAllText(o["accounting"]))!["runs"]!.Deserialize<List<StrictEntry>>(StageFolder.Json) ?? []).ToDictionary(e => e.Run, StringComparer.Ordinal);
        var report = JsonNode.Parse(File.ReadAllText(o["report"]))!["instances"]!.AsArray();
        var instances = Instances.Read(o["reports"]).ToDictionary(i => i.Instance, StringComparer.Ordinal);
        var reportsJson = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, PartFixtures.ReportsFileName)))!["reports"]!.AsArray();
        var items = new List<ControlItem>();

        TextControls(integrity, items);
        ExtractionTextControls(o["extraction"], items);
        SliceTransparency(instances.Values, integrity, sliceIntegrity, items);
        await AgreementAsync(stage, instances.Values, accounting, fixtures, sums, timeout, items, cancellation);
        foreach (var (baseInstance, id) in new[] { (AccountingBase, "accounting"), (MarksBase, "marks"), (NearEndBase, "near-end"), (NearStartBase, "near-start") })
        {
            if (!instances.TryGetValue(baseInstance, out var instance) || !instance.Complete)
            {
                items.Add(new ControlItem("report-faults", id, baseInstance, "-", [], "no", "the base instance is missing or incomplete"));
                continue;
            }

            var parts = await Instances.LoadAsync(instance, o["slices"], fixtures, sums, timeout, cancellation);
            var baseResult = ReportChecks.Check(instance.Instance, parts);
            if (baseResult.Keys().Count > 0)
            {
                items.Add(new ControlItem("report-faults", id, baseInstance, "-", baseResult.Keys(), "no", "the base fails a report rule or has a broken boundary"));
                continue;
            }

            var boundaries = reportsJson.First(r => r!["report"]!.GetValue<string>() == instance.Report)!["boundaries"]!.AsArray();
            items.AddRange(id switch
            {
                "accounting" => AccountingFaults(instance, parts, boundaries),
                "marks" => [MarksFault(instance, parts)],
                "near-end" => [MembershipFault(instance, parts)],
                _ => [DuplicatedHeaderFault(instance, parts, boundaries)],
            });
        }

        NumberingControls(o["reports"], report, instances, items);
        ProbeItems(o["probe"], items);

        var groups = new[] { "text", "slice-transparency", "agreement", "report-positive", "report-faults", "numbering", "probe" };
        var trusted = items.All(i => i.Behaved != "no") && groups.All(g => items.Any(i => i.Group == g && i.Behaved == "yes"));
        foreach (var i in items)
        {
            Console.WriteLine($"{i.Group} {i.Id} on {i.Run}: {i.Behaved} — expected [{i.Expected}], observed [{string.Join(" | ", i.Observed)}]{(i.Note is null ? string.Empty : $" ({i.Note})")}");
        }

        Console.WriteLine($"Instrument trusted: {trusted}.");
        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "controls.json"), new { trusted, items });
        return 0;
    }

    private static void TextControls(Dictionary<string, IntegrityEntry> integrity, List<ControlItem> items)
    {
        foreach (var id in new[] { "s1-grid-s1-B100-split-t1.00-f0.90-on-r1", "s1-grid-s1-K1-split-t1.00-f0.90-on-r1" })
        {
            var ti = integrity.GetValueOrDefault(id);
            items.Add(new ControlItem("text", "positive", id, "pass", Observed(ti), ti?.Verdict == "pass" ? "yes" : "no"));
        }

        foreach (var (mutation, fixture) in new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") })
        {
            var id = $"s1-grid-s1-{mutation}-{fixture}-split-t1.00-f0.90-on-r1";
            var ti = integrity.GetValueOrDefault(id);
            var (cls, row, region) = ControlMutations.Expected(mutation);
            var expected = $"{cls}{(row is null ? string.Empty : $" row {row}")}{(region is null ? string.Empty : $" {region}")}";
            var sole = ti is not null && ti.FailureCount == 1 && ti.Failures is [var only] && only.Class == cls && (row is null || only.Row == row) && (region is null || only.Region == region);
            items.Add(new ControlItem("text", mutation, id, expected, Observed(ti), sole ? "yes" : "no"));
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
            var observed = c["observed"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
            var behaved = c["behaved"]!.GetValue<string>();
            var expected = c["expected"]!.GetValue<string>();
            if (id is "E5" or "E6" or "E7" or "E8")
            {
                items.Add(new ControlItem("text", id, baseRun ?? "-", expected, observed, "not evaluable", $"the text oracle's controls add simulated marks and evaluate without the footer; its own result: {behaved}"));
                continue;
            }

            var ok = baseRun == ExtractionBase && behaved == "yes" && observed.Count == 1;
            items.Add(new ControlItem("text", id, baseRun ?? "-", expected, observed, ok ? "yes" : "no", $"its own result: {behaved}"));
        }
    }

    /// <summary>Every slice of every assembled report reads like its part's own PDF: page count, text verdict, failure list and content hash.</summary>
    private static void SliceTransparency(IEnumerable<ReportInstance> instances, Dictionary<string, IntegrityEntry> integrity, Dictionary<string, IntegrityEntry> slices, List<ControlItem> items)
    {
        var compared = 0;
        foreach (var instance in instances.Where(i => i.Complete))
        {
            for (var k = 1; k <= instance.Parts.Count; k++)
            {
                var part = instance.Parts[k - 1];
                var id = Instances.SliceId(instance.Instance, k);
                var own = integrity.GetValueOrDefault(part.Run);
                var slice = slices.GetValueOrDefault(id);
                var differences = new List<string>();
                if (own is null || slice is null)
                {
                    differences.Add(own is null ? "no text entry for the part's own PDF" : "no text entry for the slice");
                }
                else
                {
                    if (slice.Pages != part.Pages || own.Pages != part.Pages)
                    {
                        differences.Add($"pages: slice {slice.Pages}, own {own.Pages}, manifest {part.Pages}");
                    }

                    if (slice.Verdict != own.Verdict)
                    {
                        differences.Add($"verdict: slice {slice.Verdict}, own {own.Verdict}");
                    }

                    if (slice.ContentSha256 != own.ContentSha256)
                    {
                        differences.Add("content hash differs");
                    }

                    if (slice.FailureCount != own.FailureCount || !Failures(slice).SequenceEqual(Failures(own)))
                    {
                        differences.Add($"failures: slice {slice.FailureCount}, own {own.FailureCount}");
                    }
                }

                compared++;
                if (differences.Count > 0)
                {
                    items.Add(new ControlItem("slice-transparency", id, part.Run, "equal", differences, "no"));
                }
            }
        }

        items.Add(new ControlItem("slice-transparency", "summary", "every slice", "every slice equal to its part's own PDF", [$"{compared} slices compared"], compared > 0 && !items.Any(i => i.Group == "slice-transparency" && i.Behaved == "no") ? "yes" : "no"));
    }

    private static IEnumerable<string> Failures(IntegrityEntry e) =>
        (e.Failures ?? []).Select(f => $"{f.Rule}|{f.Class}|{f.Row}|{f.Field}|{f.Region}|{f.Page}");

    /// <summary>
    /// XA on every document whose opening is 0.00 (the parity runs, every H1 part 1, every H0
    /// part), read as a one-part report, agrees with rules 1-5 of the strict check; and the
    /// parity runs, as one-part reports, fail no XA or RG rule (the positive report control).
    /// </summary>
    private static async Task AgreementAsync(StageFolder stage, IEnumerable<ReportInstance> instances, Dictionary<string, StrictEntry> accounting, string fixtures, Dictionary<string, string> sums, TimeSpan timeout, List<ControlItem> items, CancellationToken cancellation)
    {
        var runs = new List<string>(ParityRuns);
        foreach (var instance in instances)
        {
            runs.AddRange(instance.Variant == ReportSpec.Independent ? instance.Parts.Select(p => p.Run) : instance.Parts.Take(1).Select(p => p.Run));
        }

        var compared = 0;
        foreach (var id in runs.Distinct(StringComparer.Ordinal))
        {
            var run = stage.Runs.GetValueOrDefault(id);
            var entry = accounting.GetValueOrDefault(id);
            if (run is not { Outcome: "ok", Pdf: not null } || entry is not { Verdict: "checked" })
            {
                items.Add(new ControlItem("agreement", "xa-vs-strict", id, "agreement", ["the run or its strict entry is missing"], "no"));
                continue;
            }

            var fixture = FixtureFile.ReadVerified(fixtures, run.FixtureId, sums);
            var doc = await Instances.LoadRunAsync(Path.Combine(stage.Directory, run.Pdf), id, fixture, timeout, cancellation);
            var result = ReportChecks.Check(id, [doc]);
            var strict = entry.FailureSet.Where(f => Account.RuleNumber(f.Split('|')[0]) <= 5)
                .Select(f => $"XA-{Account.RuleNumber(f.Split('|')[0]).ToString(CultureInfo.InvariantCulture)}|{f.Split('|')[1]}").Distinct().Order(StringComparer.Ordinal).ToList();
            var mine = result.AccountingFailures.Order(StringComparer.Ordinal).ToList();
            compared++;
            if (!mine.SequenceEqual(strict))
            {
                items.Add(new ControlItem("agreement", "xa-vs-strict", id, string.Join(" ", strict), mine, "no"));
            }

            if (ParityRuns.Contains(id))
            {
                var keys = result.Keys();
                items.Add(new ControlItem("report-positive", "one-part report", id, "-", keys, keys.Count == 0 ? "yes" : "no"));
            }
        }

        items.Add(new ControlItem("agreement", "summary", "opening 0.00 documents", "XA equal to rules 1-5 of the strict check on every one", [$"{compared} documents compared"],
            compared > 0 && !items.Any(i => i.Group == "agreement" && i.Behaved == "no") ? "yes" : "no"));
    }

    /// <summary>XC-1 … XC-6, GXR-1 … GXR-3, GXR-5 and GXR-6 on the whole-block base, at the boundary before part 3.</summary>
    private static List<ControlItem> AccountingFaults(ReportInstance instance, List<PartDoc> parts, JsonArray boundaries)
    {
        var result = new List<ControlItem>();
        const int p3 = 2;
        var g3 = parts[p3].Part.FirstGlobalPage;
        var reading3 = parts[p3].Read();
        var open = boundaries.First(b => b!["beforePart"]!.GetValue<int>() == 3)!["openGroup"]!.GetValue<string>();
        var source = parts.SelectMany(p => p.Fixture.Rows.Where(r => r.Kind == RowKind.GroupStart).Select(r => r.GroupId!)).Distinct().ToList();
        var previous = source[source.IndexOf(open) - 1];
        var opening3 = decimal.Parse(ReportChecks.FooterRight(parts[1]), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        result.Add(Fault("XC-1", instance, parts, p3, [$"XA-2|p{g3}", $"XA-3|p{g3}"], d => ShiftFooterValue(d, 1, "subtotal:")));
        result.Add(Fault("XC-2", instance, parts, p3, [$"XA-3|p{g3}", "PB-7|broken|before part 3"], d => ShiftFooterValue(d, 1, "forward:", first: true)));

        var firstRow = parts[p3].Fixture.Items.First().Sequence!.Value;
        var endPage = parts[p3].Part.FirstGlobalPage + reading3.RowPages[firstRow].Max - 1;
        result.Add(Fault("XC-4", instance, parts, p3, ["XA-1|p", $"XA-2|p{endPage}"], d =>
        {
            var kept = d.ContentLines.Where((_, i) => reading3.RowOf[i] != firstRow).ToList();
            var removed = d.ContentLines.Count - kept.Count;
            d.ContentLines.Clear();
            d.ContentLines.AddRange(kept);
            return $"removed the {removed} lines of row {firstRow} (part 3)";
        }));
        result.Add(Fault("XC-5", instance, parts, p3, [$"XA-3|p{g3}", "PB-7|broken|before part 3", "PB-7|broken|before part 4"], d =>
        {
            for (var page = 1; page <= d.Pages; page++)
            {
                var words = d.FooterWords[page - 1];
                var at = Enumerable.Range(0, words.Count - 1).Where(i => words[i].Text == "forward:").ToList();
                foreach (var i in new[] { at[0] + 1, at[^1] + 1 })
                {
                    var value = decimal.Parse(words[i].Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    words[i] = words[i] with { Text = (value - opening3).ToString("0.00", CultureInfo.InvariantCulture) };
                }
            }

            return $"every brought and carried forward of part 3 lowered by its opening {opening3.ToString("0.00", CultureInfo.InvariantCulture)}";
        }));
        result.Add(Fault("XC-6", instance, parts, p3, [$"XA-4|p{endPage}"], d =>
        {
            var (line, word) = reading3.AmountWords(firstRow)[^1];
            var words = d.ContentLines[line].Words;
            var at = words.FindIndex(w => ReferenceEquals(w, word) || w == word);
            var value = decimal.Parse(word.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            words[at] = word with { Text = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture) };
            return $"row {firstRow} amount {word.Text} -> {words[at].Text}";
        }));

        var plainPart = parts.FindIndex(p => p.Fixture.Rows.Any(r => r.Kind == RowKind.GroupStart && r.GroupId == open && !r.GroupName!.EndsWith(PartFixtures.ContinuationSuffix, StringComparison.Ordinal)));
        var closingPart = parts.FindIndex(p => p.Fixture.Rows.Any(r => r.Kind == RowKind.GroupEnd && r.GroupId == open));
        result.Add(Fault("GXR-1", instance, parts, plainPart, [$"RG-1|header-missing|{open}"], d =>
        {
            var at = FindLine(d, w => w[0].Text == open && w[^1].Text != "(cont.)");
            d.ContentLines.RemoveAt(at);
            return $"removed the plain header of {open} (part {plainPart + 1})";
        }));
        result.Add(Fault("GXR-2", instance, parts, closingPart, [$"RG-2|total-wrong|{open}"], d =>
        {
            var line = d.ContentLines[TotalLine(d, parts[closingPart], open)];
            var word = line.Words[^1];
            var value = decimal.Parse(word.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            line.Words[^1] = word with { Text = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture) };
            return $"{open} total {word.Text} -> {line.Words[^1].Text}";
        }));
        result.Add(Fault("GXR-3", instance, parts, closingPart, [$"RG-2|total-duplicated|{open}"], d =>
        {
            var at = TotalLine(d, parts[closingPart], open);
            var line = d.ContentLines[at];
            d.ContentLines.Insert(at + 1, new Line { Page = line.Page, Words = [.. line.Words] });
            return $"duplicated the total line of {open} on its page";
        }));
        result.Add(Fault("GXR-5", instance, parts, p3, ["RG-4|part-total-wrong|part 3"], d =>
        {
            var line = d.ContentLines[FindLine(d, w => w.Count >= 2 && w[0].Text == "Block" && w[1].Text == "total")];
            var word = line.Words[^1];
            var value = decimal.Parse(word.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            line.Words[^1] = word with { Text = (value + 0.01m).ToString("0.00", CultureInfo.InvariantCulture) };
            return $"part 3 block total {word.Text} -> {line.Words[^1].Text}";
        }));
        result.Add(Fault("GXR-6", instance, parts, p3, [$"RG-1|continuation-wrong|{previous}"], d =>
        {
            var line = d.ContentLines[FindLine(d, w => w[0].Text == open && w[^1].Text == "(cont.)")];
            line.Words[0] = line.Words[0] with { Text = previous };
            return $"part 3's continuation header names {previous} instead of {open}";
        }));
        return result;
    }

    /// <summary>XC-3: one "(continues)" mark of the over-tall split row removed.</summary>
    private static ControlItem MarksFault(ReportInstance instance, List<PartDoc> parts)
    {
        for (var p = 0; p < parts.Count; p++)
        {
            var reading = parts[p].Read();
            var split = reading.RowPages.Where(kv => kv.Value.Max > kv.Value.Min).OrderBy(kv => kv.Key).Select(kv => (Row: kv.Key, Page: kv.Value.Min)).FirstOrDefault();
            if (split.Row == 0)
            {
                continue;
            }

            var page = parts[p].Part.FirstGlobalPage + split.Page - 1;
            var pi = p;
            return Fault("XC-3", instance, parts, pi, [$"XA-5|p{page}"], d =>
            {
                for (var i = 0; i < d.ContentLines.Count; i++)
                {
                    if (reading.RowOf[i] == split.Row && d.ContentLines[i].Page == split.Page)
                    {
                        var removed = d.ContentLines[i].Words.RemoveAll(w => w.Text == PdfReading.Continues);
                        if (removed > 0)
                        {
                            return $"removed {removed} '(continues)' of row {split.Row} on page {page} (part {pi + 1})";
                        }
                    }
                }

                throw new InvalidOperationException($"no '(continues)' for row {split.Row}");
            });
        }

        return new ControlItem("report-faults", "XC-3", instance.Instance, "XA-5", [], "no", "the base has no split row");
    }

    /// <summary>GXR-4: the first group whose total and last row share a page, from part 2 on (the near-end case), its total moved above its last row.</summary>
    private static ControlItem MembershipFault(ReportInstance instance, List<PartDoc> parts)
    {
        for (var p = 1; p < parts.Count; p++)
        {
            var reading = parts[p].Read();
            foreach (var end in parts[p].Fixture.Rows.Select((r, i) => (r, i)).Where(x => x.r.Kind == RowKind.GroupEnd))
            {
                var group = end.r.GroupId!;
                var last = parts[p].Fixture.Rows[end.i - 1].Sequence!.Value;
                var doc = parts[p].Document;
                var totalAt = TotalLine(doc, parts[p], group);
                if (!reading.AnchorLine.TryGetValue(last, out var anchor) || doc.ContentLines[anchor].Page != doc.ContentLines[totalAt].Page)
                {
                    continue;
                }

                var pi = p;
                return Fault("GXR-4", instance, parts, pi, [$"RG-3|row-outside-group|{group}"], d =>
                {
                    var total = d.ContentLines[totalAt];
                    d.ContentLines.RemoveAt(totalAt);
                    d.ContentLines.Insert(anchor, total);
                    return $"moved the total line of {group} above its last row {last} (part {pi + 1})";
                });
            }
        }

        return new ControlItem("report-faults", "GXR-4", instance.Instance, "RG-3", [], "no", "no group with its total and last row on one page from part 2 on");
    }

    /// <summary>GXR-7: part 2's continuation header replaced by a plain header (the independent-part form).</summary>
    private static ControlItem DuplicatedHeaderFault(ReportInstance instance, List<PartDoc> parts, JsonArray boundaries)
    {
        var open = boundaries.First(b => b!["beforePart"]!.GetValue<int>() == 2)!["openGroup"]!.GetValue<string>();
        return Fault("GXR-7", instance, parts, 1, [$"RG-1|header-duplicated|{open}"], d =>
        {
            var line = d.ContentLines[FindLine(d, w => w[0].Text == open && w[^1].Text == "(cont.)")];
            line.Words.RemoveAt(line.Words.Count - 1);
            return $"part 2's continuation header of {open} printed plain";
        });
    }

    private static ControlItem Fault(string id, ReportInstance instance, List<PartDoc> parts, int part, List<string> expected, Func<PrintedDocument, string> mutate)
    {
        var copy = parts[part].Document.Clone();
        string applied;
        try
        {
            applied = mutate(copy);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException or KeyNotFoundException or FormatException)
        {
            return new ControlItem("report-faults", id, instance.Instance, string.Join("; ", expected), [], "no", "the fault could not be applied: " + e.Message);
        }

        var mutated = parts.Select((p, i) => i == part ? p.With(copy) : p).ToList();
        var observed = ReportChecks.Check(instance.Instance + "+" + id, mutated).Keys();
        var behaved = observed.Count == expected.Count && expected.All(observed.Contains) ? "yes" : "no";
        return new ControlItem("report-faults", id, instance.Instance, string.Join("; ", expected), observed, behaved, applied);
    }

    private static int FindLine(PrintedDocument d, Func<List<PdfWord>, bool> match)
    {
        var at = d.ContentLines.FindIndex(l => l.Words.Count > 0 && match(l.Words));
        return at >= 0 ? at : throw new InvalidOperationException("line not found");
    }

    /// <summary>The total line of a group in a part: the first "Group total" line after the group's last row.</summary>
    private static int TotalLine(PrintedDocument d, PartDoc part, string group)
    {
        var reading = part.With(d).Read();
        var last = part.Fixture.Items.Last(i => part.Fixture.Rows.Any(r => r.Kind == RowKind.GroupEnd && r.GroupId == group) && GroupOf(part.Fixture, i.Sequence!.Value) == group).Sequence!.Value;
        var from = reading.LastLine[last];
        for (var i = from + 1; i < d.ContentLines.Count; i++)
        {
            var w = d.ContentLines[i].Words;
            if (w.Count >= 2 && w[0].Text == "Group" && w[1].Text == "total")
            {
                return i;
            }
        }

        throw new InvalidOperationException($"no total line of {group}");
    }

    private static string? GroupOf(Fixture fixture, int sequence)
    {
        string? current = null;
        foreach (var r in fixture.Rows)
        {
            if (r.Kind == RowKind.GroupStart)
            {
                current = r.GroupId;
            }
            else if (r.Kind == RowKind.Item && r.Sequence == sequence)
            {
                return current;
            }
        }

        return null;
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
        return $"part page {page} footer value {words[at - 1].Text} {value.ToString("0.00", CultureInfo.InvariantCulture)} -> {shifted}";
    }

    /// <summary>Folder 06's verify: positive on every assembled report; NX-1 and NX-2 item by item.</summary>
    private static void NumberingControls(string reportsDirectory, JsonArray report, Dictionary<string, ReportInstance> instances, List<ControlItem> items)
    {
        var failing = new List<string>();
        var count = 0;
        foreach (var n in report.Where(n => n!["verdict"]?.GetValue<string>() == "checked"))
        {
            count++;
            foreach (var key in new[] { "verifyNumbered", "verifyUnnumbered" })
            {
                if (n![key]?["pass"]?.GetValue<bool>() != true)
                {
                    failing.Add($"{n["instance"]} {key}: {n[key]?["firstFailure"]}");
                }
            }
        }

        items.Add(new ControlItem("numbering", "positive", "every assembled report", "numbered and unnumbered pass", failing.Count == 0 ? [$"{count} reports"] : failing, count > 0 && failing.Count == 0 ? "yes" : "no"));
        var folder = Path.Combine(reportsDirectory, NumberingBase);
        var nx1 = VerifyItems.Read(Path.Combine(folder, "nx1.json"));
        items.Add(new ControlItem("numbering", "NX-1", NumberingBase, "structure pass, boxes pass, order pass, numbering fail, pixels fail", [Items(nx1)],
            nx1.Exists && nx1.Structure && nx1.Boxes != "fail" && nx1.Order && !nx1.Numbering && !nx1.Pixels ? "yes" : "no", "the unnumbered report verified as numbered"));
        var nx2 = VerifyItems.Read(Path.Combine(folder, "nx2.json"));
        var firstSwapped = instances.TryGetValue(NumberingBase, out var b) && b.Parts.Count > 2 ? b.Parts[1].FirstGlobalPage : -1;
        items.Add(new ControlItem("numbering", "NX-2", NumberingBase, $"structure pass, boxes pass, order fail (first failing page {firstSwapped}), numbering pass, pixels fail", [Items(nx2)],
            nx2.Exists && nx2.Structure && nx2.Boxes != "fail" && !nx2.Order && nx2.FirstFailingPage == firstSwapped && nx2.Numbering && !nx2.Pixels ? "yes" : "no",
            "parts 2 and 3 swapped in the manifest; outside the swapped range the output page and its reference are those of the passing positive verify of the same PDF, so the pixel failures lie inside the range"));
    }

    private static string Items(VerifyItems v) => !v.Exists ? "missing" :
        $"structure {P(v.Structure)}, boxes {v.Boxes}, order {P(v.Order)}, numbering {P(v.Numbering)}, pixels {P(v.Pixels)}, first failing page {v.FirstFailingPage}";

    private static string P(bool b) => b ? "pass" : "fail";

    private static void ProbeItems(string path, List<ControlItem> items)
    {
        if (!File.Exists(path))
        {
            items.Add(new ControlItem("probe", "hand-over probe", "XB-PROBE", "hold", [], "no", "no probe file"));
            return;
        }

        foreach (var a in JsonNode.Parse(File.ReadAllText(path))!["answers"]!.AsArray())
        {
            items.Add(new ControlItem("probe", "hand-over probe", "XB-PROBE-r1", a!["knownAnswer"]!.GetValue<string>(), [a["observed"]!.GetValue<string>()], a["holds"]!.GetValue<bool>() ? "yes" : "no"));
        }
    }

    private static List<string> Observed(IntegrityEntry? ti) =>
        ti is null ? ["missing"] : [.. (ti.Failures ?? []).Take(5).Select(f => $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Region is null ? string.Empty : $" {f.Region}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}")];
}
