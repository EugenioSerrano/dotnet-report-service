using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PdfLayoutSpike;

namespace StrictAccountingCheck;

/// <summary>
/// The "verdict" command. Its exit code covers the health of the instrument only:
/// identity, plan and recording, the grid pages equal to the grid experiment's,
/// the trusted controls, the ordinary block unchanged by the split and repeatable,
/// the semantic-source check and escaping on every run, no harness error. The
/// outcome of each run, the statements per markup and the table effects are
/// results, written to summary.json, and never set the exit code.
/// </summary>
internal static class Verdict
{
    public const string Confounded = "partial (character verdict not evaluable: oracle confounded, character check not control-proven)";

    public static async Task<int> RunAsync(IReadOnlyList<string> runsDirectories, IReadOnlyList<string> integrityPaths, string accountingPath, string controlsPath,
        IReadOnlyList<string> identityPaths, string parityPath, string comparisonPath, string summaryPath, string? harnessEnvironmentPath)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        var stages = runsDirectories.Select(StageFolder.Load).ToList();
        var integrity = new Dictionary<string, IntegrityEntry>(StringComparer.Ordinal);
        foreach (var path in integrityPaths)
        {
            foreach (var (id, e) in Files.ReadIntegrity(path))
            {
                integrity[id] = e;
            }
        }

        var accountingNode = JsonNode.Parse(File.ReadAllText(accountingPath))!;
        var accounting = Controls.ReadAccounting(accountingPath);
        var byRun = accounting.ToDictionary(e => e.Run, StringComparer.Ordinal);
        var controls = JsonNode.Parse(File.ReadAllText(controlsPath))!;
        var comparison = JsonNode.Parse(File.ReadAllText(comparisonPath))!;
        var parity = JsonNode.Parse(File.ReadAllText(parityPath))!;

        // 1 — identity and fixtures.
        foreach (var path in identityPaths)
        {
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            var bad = lines.Where(l => l.Split(' ') is var parts && parts[0] != parts[1]).ToList();
            if (lines.Count == 0 || bad.Count > 0)
            {
                reasons.Add($"identity {Path.GetFileName(path)}: {bad.Count} of {lines.Count} lines differ{(bad.Count > 0 ? ": " + string.Join("; ", bad.Take(5)) : string.Empty)}");
            }
        }

        var sums = accountingNode["checker"]?["fixtureSumsSha256"]?.GetValue<string>();
        if (sums != "db1ce1a7f0dcab7571f84aec2f437e94d8b976c46d3421269cb2c20e3d57c52c")
        {
            reasons.Add($"the fixture folder's SHA256SUMS hashes to {sums}, not to the committed list of the accounting experiment");
        }

        // 2 — plan and recording.
        if (parity["pass"]?.GetValue<bool>() != true)
        {
            reasons.Add("plan parity with the grid experiment failed");
        }

        var planned = stages.SelectMany(s => s.Plan.Runs.Select(p => (Stage: s, Planned: p))).ToList();
        if (planned.Select(p => p.Planned.RunId).Distinct(StringComparer.Ordinal).Count() != planned.Count)
        {
            reasons.Add("a run id is planned twice");
        }

        foreach (var (stage, p) in planned)
        {
            var count = stage.RecordCounts.GetValueOrDefault(p.RunId);
            if (count != 1)
            {
                reasons.Add($"planned run {p.RunId} has {count} records in runs.jsonl");
            }

            if (!integrity.ContainsKey(p.RunId))
            {
                reasons.Add($"planned run {p.RunId} has no text-integrity entry");
            }

            if (!byRun.ContainsKey(p.RunId))
            {
                reasons.Add($"planned run {p.RunId} has no accounting entry");
            }
        }

        // 3 — the grid pages are the grid experiment's.
        foreach (var row in comparison["runs"]!.AsArray())
        {
            var run = row!["run"]!.GetValue<string>();
            if (byRun.GetValueOrDefault(run)?.RunClass != "ok")
            {
                continue;
            }

            foreach (var field in new[] { "semInnerSha256", "htmlBytes" })
            {
                var f = row[field];
                if (f is null || f["here"]?.GetValue<string>() != f["reference"]?.GetValue<string>())
                {
                    reasons.Add($"grid page parity: {run} {field} {f?["here"]} against {f?["reference"]}");
                }
            }
        }

        // 4 — trusted instrument.
        if (controls["trusted"]?.GetValue<bool>() != true)
        {
            reasons.Add("the instrument is not trusted: " + string.Join("; ", controls["items"]!.AsArray()
                .Where(i => i!["behaved"]!.GetValue<string>() == "no" && (i["group"]!.GetValue<string>().EndsWith("grid", StringComparison.Ordinal) || i["group"]!.GetValue<string>() is "character-agreement" or "probe"
                    || (i["group"]!.GetValue<string>() == "strict-table" && i["run"]!.GetValue<string>().Contains("-K1-", StringComparison.Ordinal))))
                .Select(i => $"{i!["group"]} {i["id"]} on {i["run"]}")));
        }

        var trusted = controls["trusted"]?.GetValue<bool>() == true;
        var ctProven = controls["characterControlProvenTable"]?.GetValue<bool>() == true;

        // 5 — the ordinary block, per markup.
        foreach (var markup in new[] { "grid", "table" })
        {
            var off = accounting.Where(e => e.Markup == markup && e.FixtureId == "B1000" && e.Split == "off").ToList();
            var on = accounting.Where(e => e.Markup == markup && e.FixtureId == "B1000" && e.Split == "split" && e.FooterOn).ToList();
            var all = off.Concat(on).ToList();
            var splitRows = on.Select(e => e.Measurements["splitRows"]?.GetValue<int?>()).ToList();
            if (all.Count != 6 || all.Any(e => e.Verdict != "checked"))
            {
                reasons.Add($"B1000 {markup}: {all.Count(e => e.Verdict == "checked")} of 6 runs checked");
            }
            else if (splitRows.Any(s => s != 0) || all.Select(e => (e.Pages, e.RowSetsSha256, e.ContentSha256)).Distinct().Count() != 1)
            {
                reasons.Add($"B1000 {markup}: the split changed the block or the repetitions differ (split rows {string.Join(",", splitRows)}; "
                    + string.Join(" | ", all.Select(e => $"{e.Run} {e.Pages}p {e.RowSetsSha256?[..8]} {e.ContentSha256?[..8]}")) + ")");
            }
        }

        // 6 — SEM, escaping, no harness error.
        foreach (var e in accounting)
        {
            if (e.RunClass == "error")
            {
                reasons.Add($"{e.Run} ended in error: {e.RunReason}");
            }

            if (e.RunClass == "ok" && e.SemPass != true)
            {
                reasons.Add($"{e.Run}: the semantic-source check failed");
            }

            if (e.Verdict == "checked" && e.Rules.FirstOrDefault(r => r.Rule == 8)?.Outcome != "PASS")
            {
                reasons.Add($"{e.Run}: escaping and isolation failed");
            }
        }

        // Outcomes, statements and effects (results; computed only when the instrument is trusted).
        var outcomes = new List<JsonObject>();
        var attributed = new JsonArray();
        if (trusted)
        {
            foreach (var e in accounting)
            {
                var (outcome, refuting, partial, confounded) = Outcome(e, ctProven);
                if (ctProven)
                {
                    foreach (var f in e.TextFailures.Where(f => f.GeometryCandidate))
                    {
                        attributed.Add(new JsonObject
                        {
                            ["run"] = e.Run,
                            ["row"] = f.Row,
                            ["page"] = f.Page,
                            ["rule"] = f.Rule,
                            ["field"] = f.Field,
                            ["class"] = f.Class,
                            ["characterCheck"] = f.Row is { } r ? e.CharacterFailures.FirstOrDefault(c => c.Row == r)?.Class ?? "pass" : null,
                        });
                    }
                }

                outcomes.Add(new JsonObject
                {
                    ["run"] = e.Run,
                    ["markup"] = e.Markup,
                    ["fixture"] = e.FixtureId,
                    ["mutation"] = e.Mutation,
                    ["outcome"] = outcome,
                    ["refuting"] = new JsonArray([.. refuting.Select(x => (JsonNode)x)]),
                    ["partial"] = new JsonArray([.. partial.Select(x => (JsonNode)x)]),
                    ["confoundedTextFailures"] = confounded,
                });
            }
        }
        else
        {
            notes.Add("the instrument is not trusted: no outcome is computed or read");
        }

        var summary = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["environment"] = harnessEnvironmentPath is not null && File.Exists(harnessEnvironmentPath) ? JsonNode.Parse(File.ReadAllText(harnessEnvironmentPath)) : null,
            ["checker"] = accountingNode["checker"]?.DeepClone(),
            ["verdict"] = new JsonObject
            {
                ["exitCode"] = reasons.Count == 0 ? 0 : 1,
                ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)r)]),
                ["notes"] = new JsonArray([.. notes.Select(r => (JsonNode)r)]),
            },
            ["parity"] = parity.DeepClone(),
            ["controls"] = controls.DeepClone(),
            ["statements"] = trusted ? Statements(accounting, outcomes, ctProven) : null,
            ["attributedTextFailures"] = attributed,
            ["tableEffects"] = trusted ? TableEffects(accounting) : null,
            ["costs"] = Costs(accounting),
            ["comparisonWithTheGridExperiment"] = new JsonObject
            {
                ["runsCompared"] = comparison["runsCompared"]?.DeepClone(),
                ["runsWithDifferences"] = comparison["runsWithDifferences"]?.DeepClone(),
                ["baseDigestHere"] = comparison["baseDigestHere"]?.DeepClone(),
                ["baseDigestReference"] = comparison["baseDigestReference"]?.DeepClone(),
            },
            ["counts"] = new JsonObject
            {
                ["planned"] = planned.Count,
                ["classes"] = JsonSerializer.SerializeToNode(accounting.GroupBy(e => e.RunClass).ToDictionary(g => g.Key, g => g.Count())),
                ["checks"] = JsonSerializer.SerializeToNode(accounting.GroupBy(e => e.Verdict).ToDictionary(g => g.Key, g => g.Count())),
                ["outcomes"] = JsonSerializer.SerializeToNode(outcomes.GroupBy(o => $"{o["markup"]} {(o["mutation"] is null ? string.Empty : "control ")}{o["outcome"]}").ToDictionary(g => g.Key, g => g.Count())),
            },
            ["outcomes"] = new JsonArray([.. outcomes]),
        };
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(StageFolder.Json) + "\n");
        foreach (var r in reasons)
        {
            Console.WriteLine("REASON: " + r);
        }

        Console.WriteLine($"verdict exit {(reasons.Count == 0 ? 0 : 1)}; instrument trusted {trusted}; character check control-proven in table {ctProven}.");
        return reasons.Count == 0 ? 0 : 1;
    }

    /// <summary>A run's outcome (confirmed, refuted, partial, inconclusive) with its causes; controls carry "control".</summary>
    public static (string Outcome, List<string> Refuting, List<string> Partial, int Confounded) Outcome(StrictEntry e, bool ctProven)
    {
        var refuting = new List<string>();
        var partial = new List<string>();
        if (e.RunClass != "ok")
        {
            return ($"inconclusive ({e.RunClass})", refuting, partial, 0);
        }

        if (e.Verdict != "checked")
        {
            return ($"inconclusive ({e.Verdict})", refuting, partial, 0);
        }

        if (e.TextVerdict is not ("pass" or "fail"))
        {
            return ($"inconclusive (text {e.TextVerdict ?? "missing"})", refuting, partial, 0);
        }

        foreach (var rule in e.Rules.Where(r => r.Outcome == "FAIL" && r.Rule <= 8 && !Account.Observation(r.Name)))
        {
            var cause = $"rule {rule.Rule} {rule.Name} p{rule.Page}: {rule.Detail}";
            switch (rule.Rule)
            {
                case 1:
                    if (e.FullEvaluation.Where(i => i.Rule == 1).All(i => i.Placement) && e.FullEvaluation.Any(i => i.Rule == 1))
                    {
                        partial.Add(cause + " (placement only)");
                    }
                    else
                    {
                        refuting.Add(cause);
                    }

                    break;
                case 2 or 3 or 7:
                    refuting.Add(cause);
                    break;
                default:
                    partial.Add(cause);
                    break;
            }
        }

        if (e.Rule7 is { Outcome: "FAIL" } r7)
        {
            refuting.Add($"rule 7 against {e.Rule7Pair} p{r7.Page}: {r7.Detail}");
        }

        foreach (var c in e.CharacterFailures)
        {
            (c.Class == "ambiguous" ? partial : refuting).Add($"character {c.Class} row {c.Row}: {c.Detail}");
        }

        var confounded = 0;
        foreach (var f in e.TextFailures)
        {
            var text = $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Field is null ? string.Empty : $" {f.Field}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}";
            if (f.GeometryCandidate)
            {
                if (!ctProven)
                {
                    confounded++;
                }

                continue;
            }

            (f.Rule is "TI-2" or "TI-3" or "TI-5" or "TI-6" ? refuting : partial).Add(text);
        }

        if (e.TextFailureCount > e.TextFailures.Count)
        {
            refuting.Add($"{e.TextFailureCount - e.TextFailures.Count} text-integrity failures beyond those listed");
        }

        if (e.LayoutRepeated)
        {
            partial.Add("\"Layout repeated\" signalled by the layout library");
        }

        if (e.Mutation is not null)
        {
            return ("control", refuting, partial, confounded);
        }

        var outcome = refuting.Count > 0 ? "refuted"
            : confounded > 0 ? Confounded
            : partial.Count > 0 ? "partial"
            : "confirmed";
        return (outcome, refuting, partial, confounded);
    }

    private static JsonObject Statements(List<StrictEntry> accounting, List<JsonObject> outcomes, bool ctProven)
    {
        var result = new JsonObject();
        foreach (var markup in new[] { "grid", "table" })
        {
            var runs = accounting.Where(e => e.Markup == markup && e.Mutation is null).ToList();
            var ok = runs.Where(e => e.RunClass == "ok" && e.Verdict == "checked").ToList();
            var outcomeOf = outcomes.Where(o => o["markup"]!.GetValue<string>() == markup).ToDictionary(o => o["run"]!.GetValue<string>(), o => o);

            var rowsLost = ok.Where(e => e.Rules.Any(r => r.Rule == 1 && r.Outcome == "FAIL") && !(e.FullEvaluation.Where(i => i.Rule == 1).Any() && e.FullEvaluation.Where(i => i.Rule == 1).All(i => i.Placement))).Select(e => e.Run).ToList();
            var charactersLost = ok.Where(e => e.CharacterFailures.Any(c => c.Class != "ambiguous")
                || e.TextFailures.Any(f => !f.GeometryCandidate && f.Rule is "TI-2" or "TI-3" or "TI-5" or "TI-6")).Select(e => e.Run).ToList();
            var notEvaluable = markup == "table" && !ctProven ? ok.Where(e => e.TextFailures.Any(f => f.GeometryCandidate)).Select(e => e.Run).ToList() : [];
            var s1 = rowsLost.Count == 0 && charactersLost.Count == 0
                ? notEvaluable.Count == 0 ? "confirmed" : "not evaluable for some runs (oracle confounded, character check not control-proven)"
                : "refuted";

            var footerOn = ok.Where(e => e.FooterOn).ToList();
            var inexact = footerOn.Where(e => e.AccountingVerdict != "exact").Select(e => $"{e.Run}: {e.AccountingVerdict}").ToList();

            var classes = runs.Select(e => outcomeOf.GetValueOrDefault(e.Run)?["outcome"]?.GetValue<string>() ?? "missing").ToList();
            var s3 = classes.Any(c => c == "refuted") ? "refuted"
                : classes.All(c => c == "confirmed") ? "confirmed"
                : "partial";

            result[markup] = new JsonObject
            {
                ["S1RowsAndCharacters"] = new JsonObject
                {
                    ["statement"] = s1,
                    ["runsWithLostRows"] = new JsonArray([.. rowsLost.Select(x => (JsonNode)x)]),
                    ["runsWithCharacterFailures"] = new JsonArray([.. charactersLost.Select(x => (JsonNode)x)]),
                    ["runsNotEvaluable"] = new JsonArray([.. notEvaluable.Select(x => (JsonNode)x)]),
                    ["runsChecked"] = ok.Count,
                    ["runsInconclusive"] = runs.Count - ok.Count,
                },
                ["S2PerPageAccounting"] = new JsonObject
                {
                    ["statement"] = inexact.Count == 0 ? "exact" : "not exact on every run",
                    ["footerOnRunsChecked"] = footerOn.Count,
                    ["inexact"] = new JsonArray([.. inexact.Select(x => (JsonNode)x)]),
                },
                ["S3OptionB"] = new JsonObject
                {
                    ["statement"] = s3,
                    ["byOutcome"] = JsonSerializer.SerializeToNode(runs.GroupBy(e => outcomeOf.GetValueOrDefault(e.Run)?["outcome"]?.GetValue<string>() ?? "missing")
                        .ToDictionary(g => g.Key, g => g.Select(e => BaseId(e.Run)).ToList())),
                },
            };
        }

        result["note"] = "Table results hold at trigger 1.00 and part limit 0.90 only (the grid experiment's selection); no other value was measured for table markup.";
        return result;
    }

    private static JsonObject TableEffects(List<StrictEntry> accounting)
    {
        var rows = new JsonArray();
        foreach (var t in accounting.Where(e => e.Markup == "table" && e.Verdict == "checked"))
        {
            var g = accounting.FirstOrDefault(e => e.Markup == "grid" && BaseId(e.Run) == BaseId(t.Run));
            rows.Add(new JsonObject
            {
                ["run"] = t.Run,
                ["pages"] = t.Pages,
                ["gridPages"] = g?.Pages,
                ["declaredPages"] = t.Geometry.Count(x => x.Class == "declared"),
                ["otherPages"] = t.GeometryOtherPages,
                ["firstOtherPage"] = t.FirstOtherPage,
                ["otherLeftEdgesMm"] = t.Geometry.FirstOrDefault(x => x.Class == "other") is { } o ? new JsonArray([.. o.LeftEdgesMm.Select(v => (JsonNode)v)]) : null,
                ["columnHeaderPages"] = new JsonArray([.. t.HeaderRowPages.Select(p => (JsonNode)p)]),
                ["geometryCandidateTextFailures"] = t.TextFailures.Count(f => f.GeometryCandidate),
            });
        }

        return new JsonObject { ["runs"] = rows, ["note"] = "Measurements only; nothing was changed to alter them." };
    }

    /// <summary>The run id without this experiment's stage and markup prefix.</summary>
    public static string BaseId(string run)
    {
        var first = run.IndexOf('-', StringComparison.Ordinal);
        var second = run.IndexOf('-', first + 1);
        return run[(second + 1)..];
    }

    private static JsonArray Costs(List<StrictEntry> accounting) =>
        new([.. accounting.Where(e => e.RunClass == "ok").Select(e => (JsonNode)new JsonObject
        {
            ["run"] = e.Run,
            ["pages"] = e.Pages,
            ["parts"] = e.Measurements["parts"]?.DeepClone(),
            ["measureMs"] = e.Measurements["measureMs"]?.DeepClone(),
            ["splitMs"] = e.Measurements["splitMs"]?.DeepClone(),
            ["layoutMs"] = e.Measurements["layoutMs"]?.DeepClone(),
            ["pdfMs"] = e.Measurements["pdfMs"]?.DeepClone(),
            ["wallMs"] = e.Measurements["wallMs"]?.DeepClone(),
            ["chromiumTreePeakRssKiB"] = e.Measurements["chromiumTreePeakRssKiB"]?.DeepClone(),
            ["chromiumTreePeakPssKiB"] = e.Measurements["chromiumTreePeakPssKiB"]?.DeepClone(),
            ["harnessVmHwmKiB"] = e.Measurements["harnessVmHwmKiB"]?.DeepClone(),
        })]);
}
