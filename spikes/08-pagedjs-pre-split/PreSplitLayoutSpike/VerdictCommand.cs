using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PaginationFixtures;
using PdfLayoutSpike;

namespace PreSplitLayoutSpike;

/// <summary>
/// The verdict of this experiment. The exit code covers the health of the
/// instrument only: the oracle trusted under this engine (a control behaves
/// only when its expected failure is the sole failure), the semantic source
/// untouched on every run, the ordinary 1 000-record block unchanged by the
/// split, recording complete, escaping, no harness error and the reused files
/// unchanged. What the runs find about splitting rows before layout is written
/// as outcome statements and never sets the exit code. It also merges both
/// stages into one plan, one integrity file, summary.json and runs.csv.
/// </summary>
internal static partial class VerdictCommand
{
    public static async Task<int> RunAsync(
        string matrix,
        IReadOnlyList<string> runsDirectories,
        IReadOnlyList<string> integrityPaths,
        string controlsPath,
        string? selectionPath,
        string identityPath,
        string repositoryRoot,
        string fixturesDirectory,
        string outDirectory)
    {
        Directory.CreateDirectory(outDirectory);
        var reasons = new List<string>();
        var notes = new List<string>();
        var full = matrix == "full";

        // Merge the plans, the records and the integrity files of the stages.
        var plans = new List<JsonObject>();
        var stageSummaries = new JsonArray();
        var records = new List<RunRecord>();
        var runsDirectoryOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = Evidence.LoadStages(runsDirectories, integrityPaths);
        records.AddRange(loaded.Records);
        foreach (var (id, directory) in loaded.DirectoryOf)
        {
            runsDirectoryOf[id] = directory;
        }

        var integrityRuns = loaded.Integrity;
        var checkers = new JsonArray();
        foreach (var path in integrityPaths)
        {
            checkers.Add(JsonNode.Parse(await File.ReadAllTextAsync(path))!["checker"]?.DeepClone());
        }

        foreach (var (directory, stage) in loaded.Stages)
        {
            var plan = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "plan.json")))!["runs"]!.AsArray();
            foreach (var p in plan.Select(p => p!.AsObject()))
            {
                if (runsDirectories.Count > 1)
                {
                    p["runId"] = Evidence.Qualified(stage, p["runId"]!.GetValue<string>());
                }

                plans.Add(p);
            }

            var summaryPath = Path.Combine(directory, "summary.json");
            if (File.Exists(summaryPath))
            {
                stageSummaries.Add(JsonNode.Parse(await File.ReadAllTextAsync(summaryPath)));
            }
        }

        var evidence = new Evidence(records, integrityRuns);
        var mergedPlan = new JsonObject { ["matrix"] = matrix, ["runs"] = new JsonArray([.. plans.Select(p => (JsonNode)p.DeepClone())]) };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "plan.json"), mergedPlan.ToJsonString(RunRecord.JsonIndented) + "\n");
        var mergedIntegrity = new JsonObject { ["checkers"] = checkers, ["runs"] = new JsonArray([.. integrityRuns.Select(r => (JsonNode)r.DeepClone())]) };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "integrity.json"), mergedIntegrity.ToJsonString(RunRecord.JsonIndented) + "\n");
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        Fixture FixtureOf(RunRecord run)
        {
            if (!fixtures.TryGetValue(run.FixtureId, out var fixture))
            {
                fixture = FixtureFile.ReadVerified(fixturesDirectory, run.FixtureId, sums);
                fixtures[run.FixtureId] = fixture;
            }

            return run.Mutation is null ? fixture : ControlMutations.Apply(fixture, run.Mutation);
        }

        RunRecord? Find(string fixture, string split, bool footerOn, int repetition = 1, string? mutation = null, int? stage = null) =>
            records.FirstOrDefault(r => r.FixtureId == fixture && r.Split == split && (r.Footer == "accounting") == footerOn && r.Repetition == repetition && r.Mutation == mutation
                && (stage is null || r.Stage == stage));

        // Recording complete: one record and one integrity entry per planned run.
        var recordIds = records.Select(r => r.RunId).ToHashSet(StringComparer.Ordinal);
        foreach (var planned in plans)
        {
            var id = planned["runId"]!.GetValue<string>();
            if (!recordIds.Contains(id))
            {
                reasons.Add($"planned run {id} has no record in runs.jsonl");
            }
            else if (!evidence.Integrity.ContainsKey(id))
            {
                reasons.Add($"run {id} has no integrity entry");
            }
        }

        JsonNode? selection = null;
        if (full)
        {
            if (selectionPath is null || !File.Exists(selectionPath))
            {
                reasons.Add("selection.json is missing");
            }
            else
            {
                selection = JsonNode.Parse(await File.ReadAllTextAsync(selectionPath));
                var t = selection!["trigger"]!.GetValue<double>();
                var f = selection["partLimit"]!.GetValue<double>();
                foreach (var r in records.Where(r => r.Stage == 2 && r.Split == Plans.SplitOn))
                {
                    if (Plans.Format(r.Trigger ?? -1) != Plans.Format(t) || Plans.Format(r.PartLimit ?? -1) != Plans.Format(f))
                    {
                        reasons.Add($"{r.RunId}: stage 2 ran with trigger {r.Trigger} and part limit {r.PartLimit}, not the selected {t} and {f}");
                    }
                }
            }
        }

        // Oracle trust — positive controls (TI-1 ... TI-8 pass).
        var positives = full ? new[] { "B100", "K1" } : ["B100"];
        foreach (var fixture in positives)
        {
            var run = Find(fixture, Plans.SplitOn, true, stage: full ? 1 : null);
            var ti = run is null ? "missing" : evidence.TiVerdict(run);
            if (ti != "pass")
            {
                reasons.Add($"oracle untrusted: positive control {fixture} is {ti}");
            }
        }

        // Oracle trust — HTML-level negative controls: the expected failure must be the only failure.
        var controlRows = new JsonArray();
        var required = full ? new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") } : [("NC-W", "B100")];
        foreach (var (mutation, fixture) in required)
        {
            var run = Find(fixture, Plans.SplitOn, true, 1, mutation);
            var (sole, any, detail) = SoleFailure(run is null ? null : evidence.IntegrityOf(run), mutation);
            controlRows.Add(new JsonObject { ["control"] = mutation, ["base"] = fixture, ["sole"] = sole, ["anyFailureMatches"] = any, ["detail"] = detail });
            if (!sole)
            {
                reasons.Add($"oracle untrusted: control {mutation} on {fixture} — {detail}");
            }
        }

        var t300u = Find("T300u", Plans.SplitOn, true, stage: full ? 2 : null);
        foreach (var run in records.Where(r => r.Mutation is not null && r.FixtureId == "T300u"))
        {
            var (sole, any, detail) = SoleFailure(evidence.IntegrityOf(run), run.Mutation!);
            controlRows.Add(new JsonObject { ["control"] = run.Mutation, ["base"] = "T300u", ["sole"] = sole, ["anyFailureMatches"] = any, ["detail"] = detail });
            if (t300u is null || evidence.TiVerdict(t300u) != "pass")
            {
                notes.Add($"control {run.Mutation} on T300u: not evaluable (the unmutated T300u split run is {(t300u is null ? "missing" : evidence.TiVerdict(t300u))})");
            }
            else if (!sole)
            {
                reasons.Add($"oracle untrusted: control {run.Mutation} on T300u — {detail}");
            }
        }

        // Oracle trust — extraction-level controls E1 ... E8 (the expected failure the sole observed failure; E5 and E7 observe none).
        var controls = JsonNode.Parse(await File.ReadAllTextAsync(controlsPath))!;
        var baseRunId = controls["baseRun"]?.GetValue<string>();
        var ruleBase = ControlsBase(evidence, full);
        if (baseRunId != (ruleBase is null ? null : Evidence.Unqualified(ruleBase.RunId)))
        {
            reasons.Add($"oracle untrusted: the extraction-level controls ran on base {baseRunId ?? "none"}, the base rule gives {ruleBase?.RunId ?? "none"}");
        }

        var extraction = new JsonArray();
        foreach (var c in controls["controls"]!.AsArray())
        {
            var id = c!["id"]!.GetValue<string>();
            var behaved = c["behaved"]!.GetValue<string>();
            var observed = c["observed"]?.AsArray().Select(o => o!.GetValue<string>()).ToList() ?? [];
            var expected = c["expected"]!.GetValue<string>();
            var mandatory = id is "E1" or "E2" or "E3" or "E4";
            string verdict;
            string why;
            if (!mandatory)
            {
                // Never a reason for exit 1: the checker's E5-E8 add simulated marks and evaluate without the footer.
                verdict = "not evaluable";
                why = "the checker's controls cannot take real marks: they add simulated marks and evaluate without the footer";
            }
            else if (behaved == "yes" && observed.Count == 1 && ObservedIs(observed[0], expected))
            {
                verdict = "yes";
                why = "the expected failure is the only one observed";
            }
            else
            {
                verdict = "no";
                why = behaved != "yes" ? $"the checker reports '{behaved}'" : $"{observed.Count} failures observed, the expected one must be the only one: {string.Join(" | ", observed)}";
                reasons.Add($"oracle untrusted: extraction-level control {id} — {why}");
            }

            extraction.Add(new JsonObject
            {
                ["id"] = id,
                ["mutation"] = c["mutation"]?.DeepClone(),
                ["expected"] = expected,
                ["checker"] = behaved,
                ["observed"] = new JsonArray([.. observed.Select(o => (JsonNode)JsonValue.Create(o))]),
                ["sole"] = verdict,
                ["anyFailureMatches"] = behaved == "yes",
                ["reason"] = why,
            });
            if (mandatory)
            {
                notes.Add($"{id}: the checker's own result (any matching failure counts) is '{behaved}'; the sole-failure result is '{verdict}'");
            }
        }

        // TI-7 is never claimed from the controls; the real-mark runs are an observation only.
        const bool ti7Claimed = false;
        var realMarkRuns = records.Where(r => r.Outcome == "ok" && r.Footer == "accounting" && Evidence.SplitRows(r) > 0).ToList();
        var realMarkTi7Pass = realMarkRuns.Count(r => evidence.IntegrityOf(r)?["rules"]?["TI-7"]?.GetValue<string>() == "pass");
        var ti7Statement = $"TI-7 rests on the real-mark runs (footer on, at least one row split): {realMarkTi7Pass} of {realMarkRuns.Count} pass TI-7. "
            + "It is an observation only and is not control-proven: the extraction-level controls E5-E8 are not evaluable on real marks.";
        notes.Add(ti7Statement);
        var trusted = !reasons.Any(r => r.StartsWith("oracle untrusted", StringComparison.Ordinal));

        // SEM on every ok run; escaping (rule 8) on every ok run; no harness error.
        foreach (var r in records)
        {
            if (r.Outcome == "ok" && r.Sem?.Pass != true)
            {
                reasons.Add($"{r.RunId}: SEM failed (a {r.Sem?.A} b {r.Sem?.B} c {r.Sem?.C}; {r.Sem?.Detail})");
            }

            if (r.Outcome == "ok" && !Evidence.RulePasses(r, AccountingVerifier.Escaping))
            {
                reasons.Add($"{r.RunId}: rule 8 (escaping and isolation) failed — {r.Rules.FirstOrDefault(c => c.Name == AccountingVerifier.Escaping)?.Detail ?? "not evaluated"}");
            }

            if (r.Outcome == "error")
            {
                reasons.Add($"{r.RunId}: harness error — {r.Reason}");
            }
        }

        // Rule 7: footer on versus off on the same split configuration.
        var layouts = new Dictionary<string, LayoutResult?>(StringComparer.Ordinal);
        LayoutResult? LayoutOf(RunRecord r)
        {
            if (!layouts.TryGetValue(r.RunId, out var layout))
            {
                var path = r.LayoutFile is null ? null : Path.Combine(runsDirectoryOf[r.RunId], r.LayoutFile);
                layout = path is not null && File.Exists(path) ? JsonSerializer.Deserialize<LayoutResult>(File.ReadAllText(path), RunRecord.Json) : null;
                layouts[r.RunId] = layout;
            }

            return layout;
        }

        var rule7 = new Dictionary<string, CheckResult>(StringComparer.Ordinal);
        var pairs = new JsonArray();
        foreach (var off in records.Where(r => r.Footer == "none" && r.Mutation is null && r.Outcome == "ok"))
        {
            var on = records.FirstOrDefault(r => r.FixtureId == off.FixtureId && r.Split == off.Split && r.Trigger == off.Trigger && r.PartLimit == off.PartLimit
                && r.Footer == "accounting" && r.Repetition == off.Repetition && r.Mutation is null && r.Stage == off.Stage);
            if (on is null || on.Outcome != "ok" || LayoutOf(on) is not { } a || LayoutOf(off) is not { } b)
            {
                continue;
            }

            var check = AccountingVerifier.CompareLayouts(AccountingVerifier.FooterNoDisplacement, a, b);
            rule7[on.RunId] = check;
            rule7[off.RunId] = check;
            pairs.Add(new JsonObject { ["on"] = on.RunId, ["off"] = off.RunId, ["outcome"] = check.Outcome, ["detail"] = check.Detail, ["firstFailingPage"] = check.FirstFailingPage });
        }

        bool Rules1To8(RunRecord r) => Evidence.RulesPerRunPass(r) && (!rule7.TryGetValue(r.RunId, out var c) || c.Pass);

        // The ordinary 1 000-record block is unchanged by the split.
        var b1000 = new JsonObject();
        if (full)
        {
            var offRuns = Enumerable.Range(1, 3).Select(i => Find("B1000", Plans.Off, true, i)).ToList();
            var onRuns = Enumerable.Range(1, 3).Select(i => Find("B1000", Plans.SplitOn, true, i)).ToList();
            var onOff = Find("B1000", Plans.SplitOn, false);
            var all = offRuns.Concat(onRuns).Append(onOff).ToList();
            if (all.Any(r => r is null || r.Outcome != "ok"))
            {
                reasons.Add("B1000: a run is missing or not ok (" + string.Join(", ", all.Select(r => r is null ? "missing" : $"{r.RunId} {r.Outcome}")) + ")");
            }
            else
            {
                foreach (var r in onRuns.Append(onOff))
                {
                    if (Evidence.SplitRows(r!) != 0)
                    {
                        reasons.Add($"{r!.RunId}: the split split {Evidence.SplitRows(r)} rows of B1000");
                    }
                }

                foreach (var r in offRuns.Concat(onRuns))
                {
                    if (!Rules1To8(r!))
                    {
                        reasons.Add($"{r!.RunId}: rules 1-8 do not all pass — {Evidence.RuleLine(r)}");
                    }

                    if (evidence.TiVerdict(r!) != "pass")
                    {
                        reasons.Add($"{r!.RunId}: TI is {evidence.TiVerdict(r)}");
                    }

                    if (!Evidence.NoLayoutRepeated(r!))
                    {
                        reasons.Add($"{r!.RunId}: {Evidence.Signals(r)}");
                    }
                }

                if (!rule7.TryGetValue(onOff!.RunId, out var pair) || !pair.Pass)
                {
                    reasons.Add($"B1000: rule 7 does not hold on the split footer-on/off pair ({pair?.Detail ?? "not evaluated"})");
                }

                var reference = LayoutOf(offRuns[0]!);
                foreach (var r in offRuns.Skip(1).Concat(onRuns))
                {
                    var other = LayoutOf(r!);
                    var same = reference is not null && other is not null ? AccountingVerifier.CompareLayouts("same-layout", reference, other) : new CheckResult("same-layout", false, null, "layout missing");
                    if (!same.Pass)
                    {
                        reasons.Add($"B1000: {r!.RunId} differs from {offRuns[0]!.RunId} ({same.Detail}, page {same.FirstFailingPage})");
                    }
                }

                var hashes = offRuns.Concat(onRuns).Select(r => evidence.IntegrityOf(r!)?["contentSha256"]?.GetValue<string>()).Distinct().ToList();
                if (hashes.Count != 1 || hashes[0] is null)
                {
                    reasons.Add($"B1000: the normalised content text differs between runs ({string.Join(", ", hashes)})");
                }

                b1000["pages"] = offRuns[0]!.Pages;
                b1000["pagesEarlierSpike"] = 36;
                b1000["contentSha256"] = hashes.FirstOrDefault();
                b1000["off"] = Measures(offRuns!);
                b1000["on"] = Measures(onRuns!);
            }
        }

        // Identity of the reused files.
        var identity = CheckIdentity(identityPath, repositoryRoot);
        if (identity.Failures.Count > 0)
        {
            reasons.Add("reused files changed: " + string.Join(", ", identity.Failures));
        }

        // Printed accounting (PA) and empty pages, per run.
        var pa = new JsonArray();
        var paCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["matches"] = 0, ["differs"] = 0, ["ambiguous"] = 0 };
        var emptyPages = new JsonArray();
        foreach (var r in records)
        {
            var integrity = evidence.IntegrityOf(r);
            if (integrity?["pageWordCounts"] is JsonArray counts)
            {
                var zeros = counts.Select((c, i) => (c: c!.GetValue<int>(), page: i + 1)).Where(x => x.c == 0).Select(x => x.page).ToList();
                emptyPages.Add(new JsonObject { ["runId"] = r.RunId, ["pages"] = counts.Count, ["emptyPages"] = new JsonArray([.. zeros.Select(p => (JsonNode)JsonValue.Create(p))]) });
            }

            if (r.Outcome != "ok" || r.Footer != "accounting" || integrity?["verdict"]?.GetValue<string>() is not ("pass" or "fail"))
            {
                continue;
            }

            var (result, detail) = PrintedAccounting(FixtureOf(r), integrity!);
            paCounts[result] = paCounts.GetValueOrDefault(result) + 1;
            pa.Add(new JsonObject { ["runId"] = r.RunId, ["result"] = result, ["detail"] = detail });
        }

        // Outcome statements (never part of the exit code).
        var outcomes = new JsonObject();
        string RowStatement(RunRecord? on, RunRecord? off)
        {
            if (on is null)
            {
                return "inconclusive (missing)";
            }

            if (on.Outcome != "ok")
            {
                return $"inconclusive ({on.Outcome})";
            }

            var integrity = evidence.IntegrityOf(on);
            var tiRules = integrity?["rules"]?.AsObject();
            bool TiFails(string rule) => tiRules?[rule]?.GetValue<string>() == "fail";
            if (!Evidence.RulePasses(on, AccountingVerifier.Rows) || TiFails("TI-2") || TiFails("TI-3"))
            {
                return "refuted";
            }

            var quiet = Evidence.NoLayoutRepeated(on) && (off is null || Evidence.NoLayoutRepeated(off));
            return Rules1To8(on) && evidence.TiVerdict(on) == "pass" && quiet && (off is null || rule7.ContainsKey(on.RunId)) ? "confirmed" : "partial";
        }

        if (full)
        {
            var g200On = Find("G200", Plans.SplitOn, true, stage: 2);
            var g200Off = Find("G200", Plans.SplitOn, false, stage: 2);
            var g200 = RowStatement(g200On, g200Off);
            outcomes["g200WithSplit"] = new JsonObject
            {
                ["statement"] = g200,
                ["footerOn"] = g200On?.RunId,
                ["footerOff"] = g200Off?.RunId,
                ["rules"] = g200On is null ? null : Evidence.RuleLine(g200On),
                ["rule7"] = g200On is not null && rule7.TryGetValue(g200On.RunId, out var g7) ? $"{g7.Outcome} ({g7.Detail})" : "not evaluated",
                ["ti"] = g200On is null ? null : evidence.TiVerdict(g200On),
                ["signals"] = g200On is null ? null : Evidence.Signals(g200On),
            };
            var row8 = records.Where(r => r.Stage == 2 && r.MatrixRow == 8 && r.Split == Plans.SplitOn && r.Footer == "accounting").ToList();
            var perFixture = new JsonArray();
            foreach (var r in row8)
            {
                var off = records.FirstOrDefault(o => o.FixtureId == r.FixtureId && o.Split == r.Split && o.Footer == "none" && o.Repetition == r.Repetition && o.Stage == 2 && o.Mutation is null);
                perFixture.Add(new JsonObject
                {
                    ["runId"] = r.RunId,
                    ["fixture"] = r.FixtureId,
                    ["statement"] = RowStatement(r, off),
                    ["pages"] = r.Pages,
                    ["parts"] = r.SplitResult?["partsTotal"]?.DeepClone(),
                    ["tallRows"] = evidence.IntegrityOf(r)?["observations"]?["tallRows"]?.DeepClone(),
                });
            }

            var okRow8 = row8.Where(r => r.Outcome == "ok").Select(r => RowStatement(r, null)).ToList();
            var optionB = g200 == "confirmed" && okRow8.All(s => s == "confirmed") ? "confirmed" : g200 == "refuted" ? "refuted" : "partial";
            outcomes["optionB"] = new JsonObject { ["statement"] = optionB, ["rowSeries"] = perFixture };
            outcomes["selection"] = selection?.DeepClone();
            outcomes["printedAccounting"] = new JsonObject
            {
                ["form"] = "prefix form",
                ["shows"] = "the chain closes on the block total; every carried forward is the sum of a prefix of the rows in row order; rows seen spanning pages are counted on their last page",
                ["cannotShow"] = "the exact row set of each page",
                ["counts"] = JsonSerializer.SerializeToNode(paCounts),
            };
            outcomes["ti7"] = new JsonObject { ["statement"] = ti7Statement };
        }

        var exitCode = reasons.Count == 0 ? 0 : 1;
        Console.WriteLine($"Oracle {(trusted ? "trusted" : "UNTRUSTED")}; {records.Count} run records for {plans.Count} planned runs; TI-7 not claimed from the controls.");
        foreach (var n in notes)
        {
            Console.WriteLine("note: " + n);
        }

        foreach (var r in reasons)
        {
            Console.WriteLine("reason: " + r);
        }

        foreach (var (key, value) in outcomes)
        {
            Console.WriteLine($"outcome {key}: {value?["statement"]?.ToString() ?? value?["outcome"]?.ToString() ?? "-"}");
        }

        Console.WriteLine($"Verdict exit code {exitCode}.");

        var summary = new JsonObject
        {
            ["matrix"] = matrix,
            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["stages"] = stageSummaries,
            ["fixtureSumsSha256"] = Sums.HashFile(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName)),
            ["identity"] = new JsonObject { ["checked"] = identity.Checked, ["failures"] = new JsonArray([.. identity.Failures.Select(f => (JsonNode)JsonValue.Create(f))]) },
            ["verdict"] = new JsonObject
            {
                ["exitCode"] = exitCode,
                ["oracleTrusted"] = trusted,
                ["ti7Claimed"] = ti7Claimed,
                ["ti7Statement"] = ti7Statement,
                ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)JsonValue.Create(r))]),
                ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)JsonValue.Create(n))]),
            },
            ["outcomes"] = outcomes,
            ["controls"] = new JsonObject
            {
                ["htmlLevel"] = controlRows,
                ["extractionBase"] = baseRunId,
                ["extractionBaseByRule"] = ruleBase?.RunId,
                ["extractionBaseRule"] = "the G200 split-on footer-off run if it passes TI-1 ... TI-8; else the T500u split-on footer-off run; else the B1000 split-on footer-off run (E1-E4 only)",
                ["extraction"] = extraction,
            },
            ["b1000"] = b1000,
            ["rule7Pairs"] = pairs,
            ["printedAccounting"] = pa,
            ["emptyPages"] = emptyPages,
            ["runs"] = JsonSerializer.SerializeToNode(records, RunRecord.Json),
        };
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "summary.json"), summary.ToJsonString(RunRecord.JsonIndented) + "\n");
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "runs.csv"), Csv(records, evidence, rule7, pa));
        Console.WriteLine($"Merged plan, integrity, summary and runs.csv written to {outDirectory}.");
        return exitCode;
    }

    /// <summary>
    /// The base of the extraction-level controls: the G200 split-on footer-off run if it passes TI-1 ... TI-8 under its
    /// own modes; else the T500u split-on footer-off run; else the B1000 split-on footer-off run (E1-E4 only). Null when
    /// none passes. The quick loop has only the first candidate.
    /// </summary>
    public static RunRecord? ControlsBase(Evidence evidence, bool full)
    {
        RunRecord? Off(string fixture) => evidence.Records.FirstOrDefault(r => r.FixtureId == fixture && r.Split == Plans.SplitOn && r.Footer == "none"
            && r.Mutation is null && r.Repetition == 1 && r.Outcome == "ok");
        var order = full ? new[] { "G200", "T500u", "B1000" } : ["G200"];
        return order.Select(Off).FirstOrDefault(r => r is not null && evidence.TiVerdict(r) == "pass");
    }

    /// <summary>A negative control behaves only when the expected failure is the sole failure.</summary>
    private static (bool Sole, bool Any, string Detail) SoleFailure(JsonObject? integrity, string mutation)
    {
        if (integrity is null)
        {
            return (false, false, "missing");
        }

        var (expectedClass, row, region) = ControlMutations.Expected(mutation);
        var verdict = integrity["verdict"]?.GetValue<string>();
        var count = integrity["failureCount"]?.GetValue<int>() ?? 0;
        var failures = integrity["failures"]?.AsArray() ?? [];
        bool Matches(JsonNode? f) => f?["class"]?.GetValue<string>() == expectedClass
            && (row is null || f["row"]?.GetValue<int>() == row)
            && (region is null || f["region"]?.GetValue<string>() == region);
        var any = failures.Any(Matches);
        var sole = verdict == "fail" && count == 1 && Matches(failures[0]);
        var described = string.Join(" | ", failures.Take(5).Select(f => $"{f?["rule"]} {f?["class"]}" + (f?["row"] is null ? string.Empty : $" row {f["row"]}") + (f?["region"] is null ? string.Empty : $" region {f["region"]}")));
        return (sole, any, $"{verdict}, {count} failure(s){(count == 0 ? string.Empty : ": " + described)}; expected {expectedClass}{(row is null ? string.Empty : $" row {row}")}{(region is null ? string.Empty : $" region {region}")} as the only failure");
    }

    private static bool ObservedIs(string observed, string expected)
    {
        var expectedClass = expected.Split(' ', 2)[0];
        var row = RowNumber().Match(expected);
        return observed.Contains(" " + expectedClass, StringComparison.Ordinal)
            && (!row.Success || observed.Contains(row.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// The printed footers (header and footer bands as the checker read them) against the source amounts: the
    /// brought-forward / page-subtotal / carried-forward chain must close on the block total, every carried-forward
    /// value must be a sum of the source amounts of a prefix of the rows in document order (rows end in order), and a
    /// row the checker saw spanning pages must be inside the prefix of its last page and outside that of the page before.
    /// </summary>
    private static (string Result, string Detail) PrintedAccounting(Fixture fixture, JsonObject integrity)
    {
        var bands = integrity["observations"]?["bands"]?.AsArray();
        if (bands is null || bands.Count == 0)
        {
            return ("differs", "page 1: no printed footer was read");
        }

        var items = fixture.Items.ToList();
        var prefix = new List<decimal> { 0m };
        foreach (var item in items)
        {
            prefix.Add(prefix[^1] + decimal.Parse(item.Amount!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        }

        var lastPage = new Dictionary<int, int>();
        foreach (var span in (integrity["observations"]?["multiPageRows"]?.AsArray() ?? []).Concat(integrity["observations"]?["tallRows"]?.AsArray() ?? []))
        {
            lastPage[span!["row"]!.GetValue<int>()] = span["lastPage"]!.GetValue<int>();
        }

        var indexOf = items.Select((item, i) => (item.Sequence!.Value, i)).ToDictionary(x => x.Value, x => x.i);
        var carriedBefore = 0m;
        var k = 0;
        var ambiguousPages = new List<int>();
        var candidatesAt = new Dictionary<int, (int Min, int Max)>();
        foreach (var band in bands)
        {
            var page = band!["page"]!.GetValue<int>();
            if (!TryAmount(band["footerLeft"]?.GetValue<string>(), "Brought forward", out var brought)
                || !TryAmount(band["footerCentre"]?.GetValue<string>(), "Page subtotal", out var subtotal)
                || !TryAmount(band["footerRight"]?.GetValue<string>(), "Carried forward", out var carried))
            {
                return ("differs", $"page {page}: footer not readable ('{band["footerLeft"]}' | '{band["footerCentre"]}' | '{band["footerRight"]}')");
            }

            if (brought != carriedBefore)
            {
                return ("differs", $"page {page}: brought forward {brought}, the previous page carried {carriedBefore}");
            }

            if (carried != brought + subtotal)
            {
                return ("differs", $"page {page}: carried forward {carried} is not brought forward {brought} + page subtotal {subtotal}");
            }

            // Every prefix length, from the smallest still possible, whose sum is the printed carried forward.
            var candidates = Enumerable.Range(k, prefix.Count - k).Where(i => prefix[i] == carried).ToList();
            if (candidates.Count == 0)
            {
                return ("differs", $"page {page}: carried forward {carried} is not the sum of any prefix of the rows from row index {k}");
            }

            if (candidates.Count > 1)
            {
                ambiguousPages.Add(page);
            }

            k = candidates[0];
            candidatesAt[page] = (candidates[0], candidates[^1]);
            carriedBefore = carried;
        }

        if (carriedBefore != prefix[^1] || TryAmountText(fixture.BlockTotal) != prefix[^1])
        {
            return ("differs", $"the chain ends at {carriedBefore}, the block total is {fixture.BlockTotal}");
        }

        foreach (var (row, last) in lastPage)
        {
            if (!indexOf.TryGetValue(row, out var index))
            {
                continue;
            }

            if (candidatesAt.TryGetValue(last, out var atLast) && atLast.Max < index + 1)
            {
                return ("differs", $"page {last}: row {row} ends on this page, but its carried forward does not include the row's amount");
            }

            if (candidatesAt.TryGetValue(last - 1, out var before) && before.Min > index)
            {
                return ("differs", $"page {last - 1}: row {row} ends on page {last}, but this page already carries its amount");
            }
        }

        var summary = $"{bands.Count} pages; the chain closes on the block total {fixture.BlockTotal}; every carried forward is a prefix sum in row order; {lastPage.Count} rows seen spanning pages consistent with their last page";
        return ambiguousPages.Count > 0
            ? ("ambiguous", $"{summary}; the prefix is not unique on pages {string.Join(',', ambiguousPages)} (ties or zero amounts)")
            : ("matches", summary);
    }

    private static bool TryAmount(string? text, string label, out decimal value)
    {
        value = 0m;
        if (text is null || !text.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith(label.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
        {
            return false;
        }

        var match = AmountPattern().Match(text);
        return match.Success && decimal.TryParse(match.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static decimal TryAmountText(string text) => decimal.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static (int Checked, List<string> Failures) CheckIdentity(string identityPath, string repositoryRoot)
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var line in File.ReadLines(identityPath).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var path = Path.Combine(repositoryRoot, parts[1].TrimStart('*'));
            count++;
            if (!File.Exists(path))
            {
                failures.Add(parts[1] + " missing");
                continue;
            }

            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(parts[1] + " differs");
            }
        }

        return (count, failures);
    }

    private static JsonObject Measures(List<RunRecord> runs) => new()
    {
        ["pages"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.Pages))]),
        ["chromiumPeakRssMiB"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.Memory is null ? (double?)null : Math.Round(r.Memory.ChromiumTreePeakRssKiB / 1024.0, 1)))]),
        ["chromiumPeakPssMiB"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.Memory is null ? (double?)null : Math.Round(r.Memory.ChromiumTreePeakPssKiB / 1024.0, 1)))]),
        ["measureMs"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.MeasureMs))]),
        ["splitMs"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.SplitMs))]),
        ["layoutMs"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.LayoutMs is null ? (double?)null : Math.Round(r.LayoutMs.Value)))]),
        ["pdfMs"] = new JsonArray([.. runs.Select(r => (JsonNode?)JsonValue.Create(r.PdfMs is null ? (double?)null : Math.Round(r.PdfMs.Value)))]),
    };

    private static string Csv(List<RunRecord> records, Evidence evidence, Dictionary<string, CheckResult> rule7, JsonArray pa)
    {
        var paById = pa.ToDictionary(p => p!["runId"]!.GetValue<string>(), p => p!["result"]!.GetValue<string>(), StringComparer.Ordinal);
        var csv = new StringBuilder();
        csv.Append("runId,stage,row,fixture,mutation,split,trigger,partLimit,footer,outcome,reason,pages,splitRows,parts,maxPartFraction,method,rules1to5,rule6,rule7,rule8,ti,tiClasses,emptyPages,")
            .Append("layoutRepeatedError,layoutRepeatedAnyLevel,sem,pa,navigationMs,fontsMs,measureMs,splitMs,layoutMs,pagedFlowMs,pdfMs,totalMs,wallMs,")
            .Append("chromiumPeakRssKiB,chromiumPeakPssKiB,chromiumProcessesMax,driverPeakRssKiB,harnessVmHwmKiB,managedHeapPeakBytes,tmpPeakBytes,oomKillBefore,oomKillAfter,leftoverProcessesKilled,consoleTotal\n");
        foreach (var r in records)
        {
            var integrity = evidence.IntegrityOf(r);
            var splitRows = r.SplitResult?["splitRows"]?.AsArray();
            var zeros = integrity?["pageWordCounts"]?.AsArray().Count(c => c!.GetValue<int>() == 0);
            string Rule(string name) => r.Rules.FirstOrDefault(c => c.Name == name)?.Outcome ?? string.Empty;
            var values = new object?[]
            {
                r.RunId, r.Stage, r.MatrixRow, r.FixtureId, r.Mutation, r.Split, r.Trigger, r.PartLimit, r.Footer, r.Outcome, r.Reason, r.Pages,
                splitRows?.Count, r.SplitResult?["partsTotal"], splitRows?.Select(s => s?["maxPartFraction"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max(),
                splitRows is null ? null : string.Join('|', splitRows.Select(s => s?["method"]?.GetValue<string>()).Distinct()),
                r.Outcome == "ok" && r.Footer == "accounting" ? (Evidence.Rules1To5Pass(r) ? "PASS" : "FAIL") : string.Empty,
                Rule(AccountingVerifier.Geometry), rule7.TryGetValue(r.RunId, out var c7) ? c7.Outcome : string.Empty, Rule(AccountingVerifier.Escaping),
                evidence.TiVerdict(r), integrity?["failureClasses"]?.AsArray().Select(x => x!.GetValue<string>()) is { } classes ? string.Join('|', classes) : string.Empty, zeros,
                r.LayoutRepeatedError, r.LayoutRepeatedAnyLevel, r.Outcome == "ok" ? (r.Sem?.Pass == true ? "pass" : "FAIL") : string.Empty, paById.GetValueOrDefault(r.RunId),
                Round(r.NavigationMs), Round(r.FontsMs), r.MeasureMs, r.SplitMs, Round(r.LayoutMs), Round(r.PagedFlowMs), Round(r.PdfMs), Round(r.TotalMs), Round(r.WallMs),
                r.Memory?.ChromiumTreePeakRssKiB, r.Memory?.ChromiumTreePeakPssKiB, r.Memory?.ChromiumProcessesMax, r.Memory?.DriverPeakRssKiB, r.HarnessVmHwmKiB,
                r.Memory?.ManagedHeapPeakBytes, r.Memory?.TmpDiskPeakBytes, r.OomKillBefore, r.OomKillAfter, r.LeftoverProcessesKilled, r.ConsoleTotal,
            };
            csv.AppendJoin(',', values.Select(Field)).Append('\n');
        }

        return csv.ToString();
    }

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 1);

    private static string Field(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double d => d.ToString("0.####", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.Contains(',', StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    [GeneratedRegex(@"-?\d+\.\d{2}")]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"row \d+")]
    private static partial Regex RowNumber();
}
