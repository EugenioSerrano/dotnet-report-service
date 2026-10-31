using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using StrictAccountingCheck;

namespace GroupContinuityCheck;

/// <summary>
/// The "verdict" command. Its exit code covers the health of the instrument only:
/// identity, the fixtures, recording complete, the shared runs' pages equal to the
/// grid experiment's, the trusted controls, the ordinary block unchanged by the split
/// and repeatable, the semantic-source check and escaping on every run, no harness
/// error. Every outcome, statement and placement observation is a result, written
/// to summary.json, and never sets the exit code.
/// </summary>
internal static class Verdict
{
    public const string Baseline = "s1-grid-s2-G200-off-on-r1";
    public const string SurplusOnly = "refuted only by an unclassified surplus (cause unproven)";

    public static async Task<int> RunAsync(string runsDirectory, string integrityPath, string accountingPath, string groupsPath, string controlsPath,
        string anchorsPath, IReadOnlyList<string> identityPaths, string fixtureReportPath, string summaryPath, string? environmentPath)
    {
        var reasons = new List<string>();
        var stage = StageFolder.Load(runsDirectory);
        var integrity = Files.ReadIntegrity(integrityPath);
        var accountingNode = JsonNode.Parse(File.ReadAllText(accountingPath))!;
        var accounting = GroupControls.ReadAccounting(accountingPath).ToDictionary(e => e.Run, StringComparer.Ordinal);
        var groups = GroupsCommand.Read(groupsPath).ToDictionary(g => g.Run, StringComparer.Ordinal);
        var controls = JsonNode.Parse(File.ReadAllText(controlsPath))!;
        var anchors = JsonNode.Parse(File.ReadAllText(anchorsPath))!;
        var fixtureReport = JsonNode.Parse(File.ReadAllText(fixtureReportPath))!;

        foreach (var path in identityPaths)
        {
            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
            var bad = lines.Where(l => l.Split(' ') is var p && p[0] != p[1]).ToList();
            if (lines.Count == 0 || bad.Count > 0)
            {
                reasons.Add($"identity {Path.GetFileName(path)}: {bad.Count} of {lines.Count} lines differ");
            }
        }

        if (fixtureReport["selfCheck"] is null || fixtureReport["secondBuild"]?.GetValue<string>() != "identical")
        {
            reasons.Add("the fixture builder's self-check or second build did not pass");
        }

        if (accountingNode["checker"]?["fixtureSumsSha256"]?.GetValue<string>() != fixtureReport["sumsSha256"]?.GetValue<string>())
        {
            reasons.Add("the checked fixtures are not the built ones (SHA256SUMS differs)");
        }

        var planned = stage.Plan.Runs;
        if (planned.Select(p => p.RunId).Distinct(StringComparer.Ordinal).Count() != planned.Count)
        {
            reasons.Add("a run id is planned twice");
        }

        foreach (var p in planned)
        {
            if (stage.RecordCounts.GetValueOrDefault(p.RunId) != 1)
            {
                reasons.Add($"{p.RunId}: {stage.RecordCounts.GetValueOrDefault(p.RunId)} records");
            }

            if (!integrity.ContainsKey(p.RunId) || !accounting.ContainsKey(p.RunId) || !groups.ContainsKey(p.RunId))
            {
                reasons.Add($"{p.RunId}: a check entry is missing (text {integrity.ContainsKey(p.RunId)}, accounting {accounting.ContainsKey(p.RunId)}, groups {groups.ContainsKey(p.RunId)})");
            }
        }

        foreach (var row in anchors["runs"]!.AsArray())
        {
            var run = row!["run"]!.GetValue<string>();
            if (accounting.GetValueOrDefault(run)?.RunClass != "ok")
            {
                continue;
            }

            foreach (var field in new[] { "semInnerSha256", "htmlBytes" })
            {
                var f = row[field];
                if (f is null || f["here"]?.GetValue<string>() != f["reference"]?.GetValue<string>())
                {
                    reasons.Add($"grid page parity: {run} {field}");
                }
            }
        }

        var trusted = controls["trusted"]?.GetValue<bool>() == true;
        if (!trusted)
        {
            reasons.Add("the instrument is not trusted: " + string.Join("; ", controls["items"]!.AsArray()
                .Where(i => i!["behaved"]!.GetValue<string>() == "no").Select(i => $"{i!["group"]} {i["id"]} on {i["run"]}")));
        }

        var b1000 = accounting.Values.Where(e => e.FixtureId == "B1000" && e.FooterOn).ToList();
        if (b1000.Count != 6 || b1000.Any(e => e.Verdict != "checked"))
        {
            reasons.Add($"B1000: {b1000.Count(e => e.Verdict == "checked")} of 6 footer-on runs checked");
        }
        else if (b1000.Where(e => e.Split == "split").Any(e => e.Measurements["splitRows"]?.GetValue<int?>() != 0)
            || b1000.Select(e => (e.Pages, e.RowSetsSha256, e.ContentSha256)).Distinct().Count() != 1)
        {
            reasons.Add("B1000: the split changed the block or the repetitions differ");
        }

        foreach (var e in accounting.Values)
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

        foreach (var g in groups.Values.Where(g => g.Result?.FixtureTotalDefects.Count > 0))
        {
            reasons.Add($"{g.Run}: fixture defect — {string.Join("; ", g.Result!.FixtureTotalDefects)}");
        }

        var outcomes = new JsonArray();
        if (trusted)
        {
            foreach (var p in planned)
            {
                var e = accounting[p.RunId];
                var g = groups[p.RunId];
                var (outcome, refuting, partial, surplus) = Outcome(e, g, integrity.GetValueOrDefault(p.RunId));
                outcomes.Add(new JsonObject
                {
                    ["run"] = p.RunId,
                    ["fixture"] = p.FixtureId,
                    ["row"] = p.MatrixRow,
                    ["footerOn"] = p.FooterOn,
                    ["outcome"] = outcome,
                    ["refuting"] = new JsonArray([.. refuting.Select(x => (JsonNode)x)]),
                    ["partial"] = new JsonArray([.. partial.Select(x => (JsonNode)x)]),
                    ["unlistedTextFailures"] = surplus,
                });
            }
        }

        var summary = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["environment"] = environmentPath is not null && File.Exists(environmentPath) ? JsonNode.Parse(File.ReadAllText(environmentPath)) : null,
            ["checker"] = accountingNode["checker"]?.DeepClone(),
            ["fixtures"] = fixtureReport.DeepClone(),
            ["verdict"] = new JsonObject { ["exitCode"] = reasons.Count == 0 ? 0 : 1, ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)r)]) },
            ["controls"] = controls.DeepClone(),
            ["statements"] = trusted ? Statements(planned, accounting, groups, outcomes) : null,
            ["anchors"] = new JsonObject { ["runsCompared"] = anchors["runsCompared"]?.DeepClone(), ["differences"] = anchors["differences"]?.DeepClone() },
            ["placement"] = Placement(planned, groups),
            ["costs"] = new JsonArray([.. planned.Select(p => accounting[p.RunId]).Where(e => e.RunClass == "ok").Select(e => (JsonNode)new JsonObject
            {
                ["run"] = e.Run,
                ["pages"] = e.Pages,
                ["layoutMs"] = e.Measurements["layoutMs"]?.DeepClone(),
                ["wallMs"] = e.Measurements["wallMs"]?.DeepClone(),
                ["chromiumTreePeakRssKiB"] = e.Measurements["chromiumTreePeakRssKiB"]?.DeepClone(),
                ["containerRunningPeakBytes"] = e.Measurements["cgroupMemoryPeakBytes"]?.DeepClone(),
            })]),
            ["counts"] = new JsonObject
            {
                ["planned"] = planned.Count,
                ["classes"] = JsonSerializer.SerializeToNode(accounting.Values.GroupBy(e => e.RunClass).ToDictionary(x => x.Key, x => x.Count())),
                ["outcomes"] = JsonSerializer.SerializeToNode(outcomes.GroupBy(o => o!["outcome"]!.GetValue<string>()).ToDictionary(x => x.Key, x => x.Count())),
            },
            ["outcomes"] = outcomes,
        };
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(StageFolder.Json) + "\n");
        foreach (var r in reasons)
        {
            Console.WriteLine("REASON: " + r);
        }

        Console.WriteLine($"verdict exit {(reasons.Count == 0 ? 0 : 1)}; instrument trusted {trusted}.");
        return reasons.Count == 0 ? 0 : 1;
    }

    /// <summary>A run's outcome with its causes; controls carry "control"; the giant-row run without the split is the baseline exception.</summary>
    public static (string Outcome, List<string> Refuting, List<string> Partial, int Surplus) Outcome(StrictEntry e, GroupEntry g, IntegrityEntry? ti)
    {
        var refuting = new List<string>();
        var partial = new List<string>();
        if (e.RunClass != "ok")
        {
            return ($"inconclusive ({e.RunClass})", refuting, partial, 0);
        }

        if (e.Verdict != "checked" || g.Verdict != "checked" || ti is null || ti.Verdict is not ("pass" or "fail"))
        {
            return ($"inconclusive (check {e.Verdict}/{g.Verdict}/{ti?.Verdict ?? "missing"})", refuting, partial, 0);
        }

        foreach (var rule in e.Rules.Where(r => r.Outcome == "FAIL" && r.Rule <= 8 && !Account.Observation(r.Name)))
        {
            var cause = $"rule {rule.Rule} {rule.Name} p{rule.Page}: {rule.Detail}";
            if (rule.Rule == 1 && e.FullEvaluation.Any(i => i.Rule == 1) && e.FullEvaluation.Where(i => i.Rule == 1).All(i => i.Placement))
            {
                partial.Add(cause + " (placement only)");
            }
            else if (rule.Rule is 1 or 2 or 3 or 7)
            {
                refuting.Add(cause);
            }
            else
            {
                partial.Add(cause);
            }
        }

        if (e.Rule7 is { Outcome: "FAIL" } r7)
        {
            refuting.Add($"rule 7 against {e.Rule7Pair} p{r7.Page}: {r7.Detail}");
        }

        foreach (var f in g.Result!.Failures)
        {
            refuting.Add($"{f.Rule} {f.Class}{(f.Group is null ? string.Empty : $" {f.Group}")}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}: {f.Detail}");
        }

        foreach (var c in e.CharacterFailures)
        {
            (c.Class == "ambiguous" ? partial : refuting).Add($"character {c.Class} row {c.Row}: {c.Detail}");
        }

        foreach (var f in e.TextFailures)
        {
            var text = $"{f.Rule} {f.Class}{(f.Row is null ? string.Empty : $" row {f.Row}")}{(f.Page is null ? string.Empty : $" p{f.Page}")}";
            (f.Rule is "TI-2" or "TI-3" or "TI-5" or "TI-6" ? refuting : partial).Add(text);
        }

        if (e.LayoutRepeated)
        {
            partial.Add("\"Layout repeated\" signalled by the layout library");
        }

        var surplus = Math.Max(0, e.TextFailureCount - e.TextFailures.Count);
        if (e.Mutation is not null)
        {
            return ("control", refuting, partial, surplus);
        }

        if (e.Run == Baseline)
        {
            return ("baseline exception (the giant-row signature without the split)", refuting, partial, surplus);
        }

        var outcome = refuting.Count > 0 ? "refuted"
            : surplus > 0 ? SurplusOnly
            : partial.Count > 0 ? "partial"
            : "confirmed";
        return (outcome, refuting, partial, surplus);
    }

    private static string Case(string fixture) => fixture switch
    {
        _ when fixture.StartsWith("GE", StringComparison.Ordinal) => "group starting near a page end (GE)",
        _ when fixture.StartsWith("GS", StringComparison.Ordinal) => "group spanning several or many pages (GS)",
        "T300u" or "X64K" or "X1M" or "G200" or "GTL300u" or "GTF300u" => "groups containing over-tall rows (GT)",
        _ => "anchors and ordinary blocks",
    };

    private static JsonObject Statements(List<HarnessPlanned> planned, Dictionary<string, StrictEntry> accounting, Dictionary<string, GroupEntry> groups, JsonArray outcomes)
    {
        var result = new JsonObject();
        var runs = planned.Where(p => p.Mutation is null && p.RunId != Baseline).ToList();
        foreach (var byCase in runs.GroupBy(p => Case(p.FixtureId)))
        {
            var ok = byCase.Where(p => accounting[p.RunId].RunClass == "ok" && groups[p.RunId].Verdict == "checked").ToList();
            JsonObject Rule(string rule) => new()
            {
                ["statement"] = ok.All(p => groups[p.RunId].Result!.Failures.All(f => f.Rule != rule)) ? "confirmed" : "refuted",
                ["failing"] = new JsonArray([.. ok.Where(p => groups[p.RunId].Result!.Failures.Any(f => f.Rule == rule)).Select(p => (JsonNode)p.RunId)]),
            };
            var footerOn = ok.Where(p => p.FooterOn).ToList();
            result[byCase.Key] = new JsonObject
            {
                ["runs"] = byCase.Count(),
                ["runsChecked"] = ok.Count,
                ["G1Headers"] = Rule("GC-1"),
                ["G2GroupSubtotalsStaticText"] = Rule("GC-2"),
                ["G3Membership"] = Rule("GC-3"),
                ["G3BlockTotal"] = Rule("GC-4"),
                ["G4PageAccounting"] = new JsonObject
                {
                    ["statement"] = footerOn.All(p => accounting[p.RunId].AccountingVerdict == "exact") ? "exact" : "not exact on every run",
                    ["footerOnRuns"] = footerOn.Count,
                    ["inexact"] = new JsonArray([.. footerOn.Where(p => accounting[p.RunId].AccountingVerdict != "exact").Select(p => (JsonNode)$"{p.RunId}: {accounting[p.RunId].AccountingVerdict}")]),
                },
                ["outcomes"] = JsonSerializer.SerializeToNode(byCase.GroupBy(p => outcomes.First(o => o!["run"]!.GetValue<string>() == p.RunId)!["outcome"]!.GetValue<string>()).ToDictionary(x => x.Key, x => x.Count())),
            };
        }

        result["note"] = "The group-total text is static text written from the fixture: G2 shows whether it survives pagination, not whether the layout library computes it. The page subtotals and the chain (G4) are what the accounting script computes after layout.";
        return result;
    }

    private static JsonArray Placement(List<HarnessPlanned> planned, Dictionary<string, GroupEntry> groups) =>
        new([.. planned.Where(p => groups[p.RunId].Result is not null).Select(p =>
        {
            var r = groups[p.RunId].Result!;
            return (JsonNode)new JsonObject
            {
                ["run"] = p.RunId,
                ["headersAlone"] = r.HeadersAlone,
                ["headersAlonePages"] = new JsonArray([.. r.Groups.SelectMany(g => g.HeaderAlonePages.Select(x => (JsonNode)$"{g.Group} p{x}"))]),
                ["headersAsLastLine"] = new JsonArray([.. r.Groups.SelectMany(g => g.HeaderLastLinePages.Select(x => (JsonNode)$"{g.Group} p{x}"))]),
                ["headersRepeated"] = r.HeadersRepeated,
                ["continuationPagesWithoutHeader"] = r.ContinuationPagesWithoutHeader,
                ["totalsSeparated"] = r.TotalsSeparated,
                ["totals"] = new JsonArray([.. r.Groups.Where(g => g.TotalPage is not null).Select(g => (JsonNode)$"{g.Group} total p{g.TotalPage}, last row p{g.LastItemPage}: {g.TotalPlacement}")]),
                ["pagesWithoutDetail"] = new JsonArray([.. r.Pages.Where(x => x.WithoutDetail).Select(x => (JsonNode)$"p{x.Page}{(x.HeaderOnly ? " header-only" : string.Empty)} subtotal '{x.PageSubtotal}'")]),
            };
        })]);
}
