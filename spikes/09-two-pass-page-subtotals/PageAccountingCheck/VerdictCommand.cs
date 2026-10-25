using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;

namespace PageAccountingCheck;

/// <summary>accounting.json as the verdict reads it.</summary>
internal sealed class AccountingRead
{
    public JsonObject? Checker { get; set; }

    public List<AccountingEntry> Runs { get; set; } = [];
}

/// <summary>accounting-controls.json as the verdict reads it.</summary>
internal sealed class AccountingControlsRead
{
    public List<ControlResult> Controls { get; set; } = [];
}

/// <summary>
/// The verdict: exit 0 only when the instrument is healthy — identity of every
/// reused file, both oracles trusted on this harness, recording complete, the
/// ordinary anchor B1000 confirmed and deterministic, the escaping check, no pass
/// ending in a harness error. Outcomes of the other fixtures, the statements H1–H7,
/// caps, crashes and guard failures are results, never part of the exit code.
/// Control, mutation and fault runs are excluded from H1–H7 and from the per-fixture
/// read-back reliability.
/// </summary>
internal static class VerdictCommand
{
    private static readonly string[] Markups = ["table", "grid"];
    private static readonly string[] Strategies = ["stamped", "cell"];

    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> o)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        var matrix = o["matrix"];
        var runsDirectory = o["runs"];
        var plan = Files.ReadPlan(runsDirectory);
        var twoPass = Files.ReadTwoPass(runsDirectory);
        var contract = Files.ReadContractRunIds(runsDirectory);
        var integrity = Files.ReadIntegrity(o["integrity"]);
        var accounting = JsonSerializer.Deserialize<AccountingRead>(await File.ReadAllTextAsync(o["accounting"]), Files.Json) ?? new AccountingRead();
        var entries = accounting.Runs.ToDictionary(e => e.Run, StringComparer.Ordinal);
        var accountingControls = JsonSerializer.Deserialize<AccountingControlsRead>(await File.ReadAllTextAsync(o["accounting-controls"]), Files.Json) ?? new AccountingControlsRead();
        var integrityControls = JsonNode.Parse(await File.ReadAllTextAsync(o["controls"]));

        // 1 — identity.
        var identity = File.ReadAllLines(o["identity"]).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (identity.Count == 0)
        {
            reasons.Add("identity: the identity file lists no file");
        }

        foreach (var line in identity)
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != parts[1])
            {
                reasons.Add($"identity: {line}");
            }
        }

        var status = Path.Combine(o["fixtures"], OrdinaryFixtures.StatusFile);
        if (!File.Exists(status) || !File.ReadAllText(status).StartsWith("self-check: pass", StringComparison.Ordinal))
        {
            reasons.Add("identity: the supplementary-fixture self-check has no passing record");
        }

        var combined = File.ReadAllLines(Path.Combine(o["fixtures"], FixtureFile.SumsFileName)).ToHashSet(StringComparer.Ordinal);
        var shared = File.ReadAllLines(o["shared-sums"]);
        var sharedMissing = shared.Count(l => !combined.Contains(l));
        if (sharedMissing > 0 || shared.Length == 0)
        {
            reasons.Add($"identity: {sharedMissing} of {shared.Length} shared fixture sums differ in the fixture folder");
        }

        // 2 — the reused text oracle is trusted on this harness.
        var full = matrix == "full";
        foreach (var fixture in full ? new[] { "B100", "K1" } : ["B100"])
        {
            foreach (var markup in Markups)
            {
                var id = $"{fixture}-{markup}-c-r1.p1";
                if (!full && !Runs(plan, id[..^3]))
                {
                    continue;
                }

                if (integrity.GetValueOrDefault(id)?.Verdict != "pass")
                {
                    reasons.Add($"text oracle: positive control {id} is {integrity.GetValueOrDefault(id)?.Verdict ?? "missing"}");
                }
            }
        }

        if (full)
        {
            foreach (var mutation in new[] { "NC-W", "NC-L", "NC-R" })
            {
                var id = $"B100-table-c-{mutation}.p1";
                var entry = integrity.GetValueOrDefault(id);
                var (expectedClass, _, _) = ControlMutations.Expected(mutation);
                var failures = entry?.Failures ?? [];
                if (entry?.Control?.Behaved != true || entry.FailureCount != failures.Count || failures.Any(f => f.Class != expectedClass))
                {
                    reasons.Add($"text oracle: negative control {id} did not fail with {expectedClass} only ({entry?.FailureCount ?? 0} failure(s): {string.Join(", ", failures.Select(f => f.Class).Distinct())})");
                }
            }

            var baseRun = integrityControls?["baseRun"]?.GetValue<string>();
            if (baseRun != "T300u-table-c-r1.p1")
            {
                reasons.Add($"text oracle: extraction controls ran on {baseRun ?? "no base"}, expected T300u-table-c-r1.p1");
            }

            var controls = integrityControls?["controls"]?.AsArray() ?? new JsonArray();
            foreach (var id in new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" })
            {
                var control = controls.FirstOrDefault(c => c?["id"]?.GetValue<string>() == id);
                var behaved = control?["behaved"]?.GetValue<string>();
                var expected = control?["expected"]?.GetValue<string>();
                var observed = control?["observed"]?.AsArray().Count ?? 0;
                // E5 and E7 are the passing controls (their expected text reads "… pass"): no failure may be observed.
                var passing = expected == "-" || (expected?.Contains(" pass", StringComparison.Ordinal) ?? false);
                if (behaved != "yes" || observed != (passing ? 0 : 1))
                {
                    reasons.Add($"text oracle: {id} behaved {behaved ?? "missing"} with {observed} observed failure(s)");
                }
            }
        }

        // 3 — the accounting checker is trusted.
        foreach (var fixture in full ? new[] { "B100", "K1" } : ["B100"])
        {
            foreach (var markup in Markups)
            {
                foreach (var strategy in Strategies)
                {
                    var id = $"{fixture}-{markup}-{Key(strategy)}-r1";
                    if (!Runs(plan, id))
                    {
                        notes.Add($"accounting: positive control {id} not run ({NotRun(plan, id)})");
                        continue;
                    }

                    if (entries.GetValueOrDefault(id)?.Outcome != "confirmed")
                    {
                        reasons.Add($"accounting: positive control {id} is {entries.GetValueOrDefault(id)?.Outcome ?? "missing"}");
                    }
                }
            }
        }

        var faults = new JsonArray();
        foreach (var entry in accounting.Runs.Where(e => e.Fault is not null))
        {
            var expected = ExpectedSet(entry);
            var observed = entry.FailureSet.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var behaved = expected is not null && observed.SequenceEqual(expected.Order(StringComparer.Ordinal)) && (entry.Fault != "AC-7" || entry.SquareOnWhite == true);
            faults.Add(new JsonObject
            {
                ["fault"] = entry.Fault,
                ["run"] = entry.Run,
                ["expected"] = new JsonArray([.. (expected ?? []).Select(x => (JsonNode?)x)]),
                ["observed"] = new JsonArray([.. observed.Select(x => (JsonNode?)x)]),
                ["squareOnWhite"] = entry.SquareOnWhite,
                ["behaved"] = behaved ? "yes" : "no",
            });
            if (!behaved)
            {
                reasons.Add($"accounting: control {entry.Run} observed [{string.Join(", ", observed)}], expected exactly [{string.Join(", ", expected ?? [])}]" + (entry.Fault == "AC-7" ? $", square on white {entry.SquareOnWhite?.ToString() ?? "unknown"}" : string.Empty));
            }
        }

        if (full)
        {
            var plannedFaults = plan.Runs.Count(r => r.Fault is not null);
            if (faults.Count != plannedFaults || plannedFaults != 11)
            {
                reasons.Add($"accounting: {faults.Count} fault runs judged of {plannedFaults} planned (11 expected)");
            }

            foreach (var control in accountingControls.Controls.Where(c => c.Counted))
            {
                if (control.Behaved != "yes")
                {
                    reasons.Add($"accounting: control {control.Id} behaved {control.Behaved} ({control.Detail})");
                }
            }

            foreach (var id in new[] { "BX-1", "BX-2", "XC-1", "XC-2" }.Where(id => accountingControls.Controls.All(c => c.Id != id)))
            {
                reasons.Add($"accounting: control {id} is missing");
            }
        }

        // 4 — recording complete.
        foreach (var planned in plan.Runs)
        {
            if (!twoPass.TryGetValue(planned.Run, out var record))
            {
                reasons.Add($"recording: no run record for {planned.Run}");
                continue;
            }

            if (!entries.ContainsKey(planned.Run))
            {
                reasons.Add($"recording: no accounting entry for {planned.Run}");
            }

            var expectedPdfs = new List<string> { planned.Run + ".p2" };
            if (planned.Strategy == "cell")
            {
                expectedPdfs.Add(planned.Run + ".p1");
            }

            foreach (var pdf in expectedPdfs)
            {
                if (!contract.Contains(pdf))
                {
                    reasons.Add($"recording: {pdf} is not in runs.jsonl");
                }
                else if (!integrity.ContainsKey(pdf))
                {
                    reasons.Add($"recording: no text-integrity entry for {pdf}");
                }
            }

            // 6 — escaping and no harness error.
            if (record.Pass1.Outcome == "ok" && (record.Pass1.Dialogs != 0 || record.Pass1.AbortedRequests != 0 || record.Pass1.Scripts != 0))
            {
                reasons.Add($"escaping: {planned.Run} pass 1 dialogs {record.Pass1.Dialogs}, aborted {record.Pass1.AbortedRequests}, scripts {record.Pass1.Scripts}");
            }

            if (record.Pass1.Outcome == "error" || record.Pass2.Outcome == "error")
            {
                reasons.Add($"harness error: {planned.Run} — {record.Pass1.Reason ?? record.Pass2.Reason}");
            }
        }

        // 4 (continued) — B1: every replaced run has its "not-run" record naming its replacement, and the replacement is planned.
        var plannedIds = plan.Runs.Select(r => r.Run).ToHashSet(StringComparer.Ordinal);
        foreach (var replaced in plan.Replaced)
        {
            var record = twoPass.GetValueOrDefault(replaced.Run);
            if (record is null || record.Pass1.Outcome != "not-run" || record.Pass1.Reason?.Contains($"replaced by {replaced.ReplacedBy}", StringComparison.Ordinal) != true || !plannedIds.Contains(replaced.ReplacedBy))
            {
                reasons.Add($"recording: replaced run {replaced.Run} has no matching not-run record or its replacement {replaced.ReplacedBy} is not planned");
            }
        }

        notes.Add($"plan: {plan.Runs.Count} entries ({plan.Runs.Count(r => r.NotRunReason is not null)} not run for the position check), {plan.Replaced.Count} replaced runs, stamped in {string.Join('+', plan.StampedMarkups)}; {twoPass.Count} run records");
        if (full && plan.Runs.Count != 105)
        {
            reasons.Add($"recording: the plan has {plan.Runs.Count} entries, 105 expected");
        }

        if (twoPass.Count != plan.Runs.Count + plan.Replaced.Count)
        {
            reasons.Add($"recording: {twoPass.Count} run records for {plan.Runs.Count} plan entries and {plan.Replaced.Count} replaced runs");
        }

        var runEntries = plan.Runs.Where(r => r.NotRunReason is null).ToList();
        var notRunRecords = twoPass.Values.Count(r => r.Pass1.Outcome == "not-run");
        var combinations = runEntries.GroupBy(r => $"{r.Markup} {r.Strategy}").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        notes.Add($"counts: entries {plan.Runs.Count}, run {runEntries.Count}, planned not-run {plan.Runs.Count - runEntries.Count}, replaced {plan.Replaced.Count}, records {twoPass.Count} ({notRunRecords} not-run); "
            + string.Join(", ", combinations.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} {kv.Value}")));
        if (full && plan.StampedMarkups.Count == 1 && plan.StampedMarkups[0] == "grid")
        {
            // The plan the specification states for strategy "stamped" in grid only.
            var expectedCounts = (Entries: 105, Run: 94, NotRun: 11, Replaced: 35, Records: 140, NotRunRecords: 46, TableCell: 46, GridCell: 8, GridStamped: 40);
            var actualCounts = (Entries: plan.Runs.Count, Run: runEntries.Count, NotRun: plan.Runs.Count - runEntries.Count, Replaced: plan.Replaced.Count, Records: twoPass.Count, NotRunRecords: notRunRecords,
                TableCell: combinations.GetValueOrDefault("table cell"), GridCell: combinations.GetValueOrDefault("grid cell"), GridStamped: combinations.GetValueOrDefault("grid stamped"));
            if (actualCounts != expectedCounts || combinations.ContainsKey("table stamped"))
            {
                reasons.Add($"recording: plan counts {actualCounts} differ from {expectedCounts}");
            }
        }

        // Stop-and-ask conditions of the stamping library's side effects: the colour check and qpdf --check of every pass-2 PDF.
        foreach (var entry in accounting.Runs.Where(e => e.ColourFailures.Count > 0))
        {
            // The two faults that paint outside the reserved area (AC-7) and over ink (AC-8) must fail the colour check
            // exactly as their grayscale expected set says; any other colour failure is a stop-and-ask.
            var colour = entry.ColourFailures.Select(f => $"{(f.Class == "stamp-over-ink" ? "UO-2" : "UO-1")}|{f.Class}|p{f.Page}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (entry.Fault is "AC-7" or "AC-8" && ExpectedSet(entry) is { } faultSet && colour.SequenceEqual(faultSet.Order(StringComparer.Ordinal)))
            {
                notes.Add($"colour check of the control {entry.Run} fails exactly as its expected set ({string.Join(", ", colour)})");
                continue;
            }

            reasons.Add($"stop and ask: colour check failed on {entry.Run}: {string.Join("; ", entry.ColourFailures.Take(3).Select(f => $"{f.Class} p{f.Page}"))}");
        }

        foreach (var entry in accounting.Runs.Where(e => e.Structure is { Verdict: StampingSideEffects.Fail }))
        {
            reasons.Add($"stop and ask: qpdf --check classifies {entry.Run}.p2 fail ({entry.Structure!.Reason})");
        }

        notes.Add("structure of pass-2 PDFs: " + string.Join(", ", accounting.Runs.Where(e => e.Structure is not null).GroupBy(e => e.Structure!.Verdict).Select(g => $"{g.Count()} {g.Key}")));

        // 5 — the ordinary anchor.
        var determinism = new JsonArray();
        var groups = new List<(string Name, List<string> Runs)>();
        if (full)
        {
            foreach (var markup in Markups)
            {
                foreach (var strategy in Strategies)
                {
                    var runs = Enumerable.Range(1, 3).Select(r => $"B1000-{markup}-{Key(strategy)}-r{r}").ToList();
                    if (!runs.All(id => Runs(plan, id)))
                    {
                        notes.Add($"anchor: B1000 {markup} {strategy} not run ({NotRun(plan, runs[0])})");
                        continue;
                    }

                    groups.Add(($"B1000 {markup} {strategy}", runs));
                    foreach (var id in runs.Where(id => entries.GetValueOrDefault(id)?.Outcome != "confirmed"))
                    {
                        reasons.Add($"anchor: {id} is {entries.GetValueOrDefault(id)?.Outcome ?? "missing"}");
                    }
                }

                var x1m = Runs(plan, $"X1M-{markup}-s-r1") ? "s" : "c";
                groups.Add(($"X1M {markup} {(x1m == "s" ? "stamped" : "cell")}", [.. Enumerable.Range(1, 3).Select(r => $"X1M-{markup}-{x1m}-r{r}")]));
            }

            var f1m = Runs(plan, "F1M-b-table-s-r1") ? "s" : "c";
            groups.Add(($"F1M-b table {(f1m == "s" ? "stamped" : "cell")}", [.. Enumerable.Range(1, 3).Select(r => $"F1M-b-table-{f1m}-r{r}")]));
            groups.Add(($"F1M-a table {(f1m == "s" ? "stamped" : "cell")}", [.. Enumerable.Range(1, 3).Select(r => $"F1M-a-table-{f1m}-r{r}")]));
        }

        foreach (var (name, runs) in groups)
        {
            var (deterministic, detail) = Deterministic(runs.Select(id => entries.GetValueOrDefault(id)).ToList());
            determinism.Add(new JsonObject { ["group"] = name, ["deterministic"] = deterministic, ["detail"] = detail });
            if (name.StartsWith("B1000", StringComparison.Ordinal) && !deterministic)
            {
                reasons.Add($"anchor: {name} is not deterministic — {detail}");
            }
        }

        var statements = Statements(accounting.Runs, twoPass, determinism, o.GetValueOrDefault("harness-summary"));
        var exitCode = reasons.Count == 0 ? 0 : 1;
        if (exitCode == 0)
        {
            notes.Add(full
                ? $"Both oracles trusted; {twoPass.Count} run records for {plan.Runs.Count} planned runs; B1000 confirmed and deterministic."
                : $"Quick plan: {twoPass.Count} run records for {plan.Runs.Count} planned runs; the controls of this plan behaved (development loop, never evidence).");
        }

        foreach (var reason in reasons)
        {
            Console.WriteLine($"REASON: {reason}");
        }

        foreach (var note in notes)
        {
            Console.WriteLine(note);
        }

        Console.WriteLine($"Verdict exit code {exitCode}.");
        if (o.TryGetValue("summary", out var summaryPath))
        {
            await WriteSummaryAsync(summaryPath, o, matrix, exitCode, reasons, notes, identity, faults, determinism, statements, accounting, accountingControls, integrityControls, plan, entries, integrity);
        }

        return exitCode;
    }

    private static char Key(string strategy) => strategy == "stamped" ? 's' : 'c';

    /// <summary>Whether a run id is planned and actually run (not held back by the position check).</summary>
    private static bool Runs(PlanFile plan, string id) => plan.Runs.Any(r => r.Run == id && r.NotRunReason is null);

    private static string NotRun(PlanFile plan, string id) =>
        plan.Runs.FirstOrDefault(r => r.Run == id)?.NotRunReason ?? plan.Replaced.FirstOrDefault(r => r.Run == id)?.Reason ?? "not planned";

    /// <summary>The exact failure set of each fault (C.6), with pages from this checker's own reading of the pass-1 PDF.</summary>
    public static List<string>? ExpectedSet(AccountingEntry e) => e.Fault switch
    {
        "AC-1" => e.Pass2Pages is { } last ? ["end-page-subtotals|p2", $"brought-carried-chain|p{last}"] : null,
        "AC-2" => ["brought-carried-chain|p3"],
        "AC-3" => e.TallRowFirstPage is { } first ? [$"end-page-subtotals|p{first}"] : null,
        "AC-4" => e.TallRowFirstPage is { } first ? [$"continues-marks|p{first + 1}"] : null,
        "AC-5" => e.TallRowLastPage is { } end ? [$"continues-marks|p{end}"] : null,
        "AC-6" => e.TallRowFirstPage is { } first ? [$"visible-amounts|p{first}"] : null,
        "AC-7" => ["UO-1|outside-reserved|p1"],
        "AC-8" => e.FirstRowPage is { } page ? [$"UO-1|outside-reserved|p{page}", $"UO-2|stamp-over-ink|p{page}"] : null,
        "RB-1" => ["RB-b|readback-page|50"],
        "RB-2" => ["RB-a|readback-missing|51"],
        "RB-3" => ["RB-c|readback-order"],
        _ => null,
    };

    /// <summary>F.4: the same pass-1 page count and word counts, read-back map, stamped footer texts, pass-2 content hash and page renders.</summary>
    private static (bool Deterministic, string Detail) Deterministic(List<AccountingEntry?> runs)
    {
        if (runs.Count == 0 || runs.Any(r => r is null || r.Verdict != "checked"))
        {
            return (false, "not every repetition was checked");
        }

        var differences = new List<string>();
        void Same<T>(string name, Func<AccountingEntry, T> value)
        {
            var values = runs.Select(r => JsonSerializer.Serialize(value(r!))).Distinct(StringComparer.Ordinal).Count();
            if (values > 1)
            {
                differences.Add(name);
            }
        }

        Same("pass-1 pages", r => r.Pass1Pages);
        Same("pass-1 page word counts", r => r.Pass1PageWordCounts);
        Same("read-back map", r => r.ReadBackMapSha256);
        Same("stamped footer texts", r => r.FooterTextsSha256);
        Same("pass-2 content", r => r.ContentSha256);
        Same("pass-2 page renders", r => r.RenderSha256);
        return (differences.Count == 0, differences.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{runs.Count} repetitions identical: {runs[0]!.Pass1Pages} pages, content {runs[0]!.ContentSha256?[..12]}, map {runs[0]!.ReadBackMapSha256?[..12]}")
            : "differ in " + string.Join(", ", differences));
    }

    /// <summary>H1–H7 from the accounting entries; control, mutation and fault runs excluded.</summary>
    private static JsonObject Statements(List<AccountingEntry> all, Dictionary<string, TwoPassLine> twoPass, JsonArray determinism, string? harnessSummaryPath)
    {
        var runs = all.Where(e => e.Fault is null && e.Mutation is null && e.Reason?.StartsWith("stamped not run", StringComparison.Ordinal) != true).ToList();
        var statements = new JsonObject
        {
            ["notMeasured"] = new JsonArray([.. all.Where(e => e.Reason?.StartsWith("stamped not run", StringComparison.Ordinal) == true).Select(e => (JsonNode?)$"{e.Run}: {e.Reason}")]),
        };

        var h1 = new JsonObject();
        foreach (var markup in Markups)
        {
            foreach (var strategy in Strategies)
            {
                var set = runs.Where(e => e.Markup == markup && e.Strategy == strategy).ToList();
                if (set.Count == 0)
                {
                    continue;
                }

                var refuted = set.Where(e => e.Outcome == "refuted").ToList();
                var other = set.Where(e => e.Outcome != "confirmed").ToList();
                h1[$"{markup} {strategy}"] = new JsonObject
                {
                    ["statement"] = refuted.Count > 0 ? "refuted" : other.Count == 0 ? "confirmed" : "partial",
                    ["runs"] = set.Count,
                    ["confirmed"] = set.Count(e => e.Outcome == "confirmed"),
                    ["notConfirmed"] = new JsonArray([.. other.Select(e => (JsonNode?)$"{e.Run}: {e.Outcome} — {string.Join("; ", e.Refuting.Concat(e.Placement).Take(4))}")]),
                };
            }
        }

        statements["H1"] = h1;

        var h2 = new JsonObject();
        var allReliable = true;
        foreach (var fixture in runs.Select(e => e.FixtureId).Distinct(StringComparer.Ordinal))
        {
            var set = runs.Where(e => e.FixtureId == fixture && e.Pass1Outcome == "ok").ToList();
            var misses = set.Where(e => e.Pass2Outcome == "guard-failed" || e.ReadBack.Any(f => f.Check != "RB-d")).ToList();
            var unjudged = set.Where(e => e.ReadBackReliable is null && e.Pass2Outcome != "guard-failed").ToList();
            var verdict = misses.Count > 0 ? "not reliable" : unjudged.Count > 0 || set.Count == 0 ? "not established" : "reliable";
            allReliable &= verdict == "reliable";
            h2[fixture] = new JsonObject
            {
                ["statement"] = verdict,
                ["okRuns"] = set.Count,
                ["rowsJudged"] = set.Sum(e => e.ReadBackRowsJudged ?? 0),
                ["misses"] = new JsonArray([.. misses.SelectMany(e => e.Pass2Outcome == "guard-failed"
                    ? new[] { $"{e.Run}: readback-guard" }
                    : e.ReadBack.Where(f => f.Check != "RB-d").Select(f => $"{e.Run}: {f.Check} {f.Class} {f.Detail}")).Take(50).Select(x => (JsonNode?)x)]),
                ["positionReported"] = new JsonArray([.. set.SelectMany(e => e.ReadBack.Where(f => f.Check == "RB-d").Select(f => $"{e.Run}: {f.Detail}")).Take(20).Select(x => (JsonNode?)x)]),
                ["unjudged"] = new JsonArray([.. unjudged.Select(e => (JsonNode?)$"{e.Run}: {e.Outcome}")]),
            };
        }

        statements["H2"] = new JsonObject { ["overall"] = allReliable ? "reliable" : "not reliable on every fixture", ["fixtures"] = h2 };

        var checkedRuns = runs.Where(e => e.Verdict == "checked" && e.Pass2Outcome == "ok").ToList();
        var moved = checkedRuns.Where(e => e.Unchanged.Count > 0 || e.Rules.Any(r => r.Rule == 7 && r.Outcome == "FAIL")).ToList();
        statements["H3"] = new JsonObject
        {
            ["statement"] = moved.Count == 0 && checkedRuns.Count > 0 ? "confirmed" : moved.Count > 0 ? "refuted" : "inconclusive",
            ["runsChecked"] = checkedRuns.Count,
            ["changedPixelsInsideTheReservedArea"] = checkedRuns.Sum(e => e.ChangedPixels ?? 0),
            ["boxVerdicts"] = new JsonObject([.. checkedRuns.GroupBy(e => e.BoxVerdict ?? "none").Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))]),
            ["failures"] = new JsonArray([.. moved.SelectMany(e => e.Unchanged.Select(f => $"{e.Run}: {f.Check} {f.Class} p{f.Page} {f.Detail}")).Take(50).Select(x => (JsonNode?)x)]),
        };

        statements["H4"] = determinism.DeepClone();

        var h5 = new JsonArray();
        if (harnessSummaryPath is not null && File.Exists(harnessSummaryPath) && JsonNode.Parse(File.ReadAllText(harnessSummaryPath))?["runs"] is JsonArray harnessRuns)
        {
            foreach (var r in harnessRuns)
            {
                if (r is null || r["fault"] is not null || r["mutation"] is not null)
                {
                    continue;
                }

                h5.Add(new JsonObject
                {
                    ["run"] = r["run"]?.DeepClone(),
                    ["p1"] = r["pass1"]?["outcome"]?.DeepClone(),
                    ["p1Pages"] = r["pass1"]?["pages"]?.DeepClone(),
                    ["p1PdfMs"] = r["pass1"]?["pdfMs"]?.DeepClone(),
                    ["p1ChromiumRssKiB"] = r["pass1"]?["memory"]?["chromiumTreePeakRssKiB"]?.DeepClone(),
                    ["p1ChromiumPssKiB"] = r["pass1"]?["memory"]?["chromiumTreePeakPssKiB"]?.DeepClone(),
                    ["p2"] = r["pass2"]?["outcome"]?.DeepClone(),
                    ["p2OpenMs"] = r["pass2"]?["openMs"]?.DeepClone(),
                    ["p2ReadMs"] = r["pass2"]?["readMs"]?.DeepClone(),
                    ["p2ComputeMs"] = r["pass2"]?["computeMs"]?.DeepClone(),
                    ["p2StampMs"] = r["pass2"]?["stampMs"]?.DeepClone(),
                    ["p2SaveMs"] = r["pass2"]?["saveMs"]?.DeepClone(),
                    ["p2VmHwmKiB"] = r["pass2"]?["harnessVmHwmKiB"]?.DeepClone(),
                    ["p2ManagedHeapBytes"] = r["pass2"]?["memory"]?["managedHeapPeakBytes"]?.DeepClone(),
                    ["p2PdfBytes"] = r["pass2"]?["pdfBytes"]?.DeepClone(),
                });
            }
        }

        statements["H5"] = h5;

        var h6 = new JsonArray();
        foreach (var e in runs.Where(e => e.PagesWithoutDetail.Count > 0))
        {
            h6.Add(new JsonObject
            {
                ["run"] = e.Run,
                ["strategy"] = e.Strategy,
                ["markup"] = e.Markup,
                ["outcome"] = e.Outcome,
                ["headerOnlyPages"] = e.HeaderOnlyPages,
                ["freeTextPages"] = e.PagesWithoutDetail.Count(p => p.Kind == "free-text"),
                ["subtotalZeroOnAll"] = e.PagesWithoutDetail.All(p => p.SubtotalZero == true),
                ["carriedEqualsBroughtOnAll"] = e.PagesWithoutDetail.All(p => p.CarriedEqualsBrought == true),
                ["headerOnly"] = new JsonArray([.. e.PagesWithoutDetail.Where(p => p.Kind == "header-only").Take(30)
                    .Select(p => (JsonNode?)$"p{p.Page}: {p.Brought} | {p.Subtotal} | {p.Carried}{(p.Mark ? " | (continues)" : string.Empty)}")]),
                ["placement"] = new JsonArray([.. e.Placement.Select(x => (JsonNode?)x)]),
            });
        }

        statements["H6"] = h6;

        var h7 = new JsonObject();
        foreach (var markup in Markups)
        {
            foreach (var strategy in Strategies)
            {
                var placements = runs.Where(e => e.Markup == markup && e.Strategy == strategy).SelectMany(e => e.AmountPlacements.Select(a => (e.Run, a))).ToList();
                if (placements.Count == 0)
                {
                    continue;
                }

                h7[$"{markup} {strategy}"] = new JsonObject
                {
                    ["splitRows"] = placements.Count,
                    ["onLastPage"] = placements.Count(p => p.a.Position == "last"),
                    ["onFirstPage"] = placements.Count(p => p.a.Position == "first"),
                    ["onOtherPage"] = placements.Count(p => p.a.Position == "other"),
                    ["onLastLine"] = placements.Count(p => p.a.OnLastLine == true),
                    ["rows"] = new JsonArray([.. placements.Take(40).Select(p => (JsonNode?)$"{p.Run} row {p.a.Row}: pages {p.a.FirstPage}–{p.a.LastPage}, amount p{p.a.AmountPage} ({p.a.Position}), last line {p.a.OnLastLine}")]),
                };
            }
        }

        statements["H7"] = h7;
        _ = twoPass;
        return statements;
    }

    private static async Task WriteSummaryAsync(
        string path, IReadOnlyDictionary<string, string> o, string matrix, int exitCode, List<string> reasons, List<string> notes, List<string> identity,
        JsonArray faults, JsonArray determinism, JsonObject statements, AccountingRead accounting, AccountingControlsRead accountingControls, JsonNode? integrityControls,
        PlanFile plan, Dictionary<string, AccountingEntry> entries, Dictionary<string, IntegrityEntry> integrity)
    {
        JsonNode? harness = null;
        if (o.GetValueOrDefault("harness-summary") is { } harnessPath && File.Exists(harnessPath))
        {
            harness = JsonNode.Parse(await File.ReadAllTextAsync(harnessPath));
        }

        JsonNode? environment = null;
        if (o.GetValueOrDefault("environment") is { } environmentPath && File.Exists(environmentPath))
        {
            environment = JsonNode.Parse(await File.ReadAllTextAsync(environmentPath));
        }

        var harnessRuns = (harness?["runs"] as JsonArray)?.Where(r => r is not null).ToDictionary(r => r!["run"]!.GetValue<string>(), r => r, StringComparer.Ordinal)
            ?? new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var runs = new JsonArray();
        foreach (var planned in plan.Runs)
        {
            runs.Add(new JsonObject
            {
                ["run"] = planned.Run,
                ["harness"] = harnessRuns.GetValueOrDefault(planned.Run)?.DeepClone(),
                ["accounting"] = entries.TryGetValue(planned.Run, out var e) ? JsonSerializer.SerializeToNode(e, Files.Json) : null,
                ["integrityPass1"] = integrity.GetValueOrDefault(planned.Run + ".p1")?.Verdict,
                ["integrityPass2"] = integrity.GetValueOrDefault(planned.Run + ".p2")?.Verdict,
                ["integrityPass2Classes"] = new JsonArray([.. (integrity.GetValueOrDefault(planned.Run + ".p2")?.Failures ?? []).Select(f => f.Class).Distinct().Select(x => (JsonNode?)x)]),
            });
        }

        var summary = new JsonObject
        {
            ["matrix"] = matrix,
            ["environment"] = new JsonObject
            {
                ["harness"] = harness?["environment"]?.DeepClone(),
                ["host"] = environment?.DeepClone(),
                ["accountingChecker"] = accounting.Checker?.DeepClone(),
            },
            ["verdict"] = new JsonObject
            {
                ["exitCode"] = exitCode,
                ["reasons"] = new JsonArray([.. reasons.Select(x => (JsonNode?)x)]),
                ["notes"] = new JsonArray([.. notes.Select(x => (JsonNode?)x)]),
            },
            ["identity"] = new JsonArray([.. identity.Select(x => (JsonNode?)x)]),
            ["controls"] = new JsonObject
            {
                ["textIntegrity"] = integrityControls?.DeepClone(),
                ["accounting"] = JsonSerializer.SerializeToNode(accountingControls.Controls, Files.Json),
                ["faults"] = faults.DeepClone(),
            },
            ["determinism"] = determinism.DeepClone(),
            ["statements"] = statements,
            ["outcomes"] = new JsonObject([.. accounting.Runs.GroupBy(r => r.Outcome).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, g.Count()))]),
            ["runs"] = runs,
        };
        await File.WriteAllTextAsync(path, summary.ToJsonString(Files.Json) + "\n");
    }
}
