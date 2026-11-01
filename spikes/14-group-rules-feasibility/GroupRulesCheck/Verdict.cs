using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using StrictAccountingCheck;

namespace GroupRulesCheck;

/// <summary>
/// The "verdict" command. Its exit code covers the health of the instrument only:
/// identity and the observation definitions unchanged, the fixtures equal to those of
/// spikes/12-group-continuity, recording complete, the runs without behaviours equal
/// to that folder's, the same body in every configuration, the trusted controls, the
/// repetitions agreeing, the semantic-source check and escaping on every run, no
/// harness error, the runner's copy changed only as declared. Every outcome,
/// observation, class and cost is a result, written to summary.json, and never sets
/// the exit code.
/// </summary>
internal static class Verdict
{
    public const string SurplusOnly = "refuted only by an unclassified surplus (cause unproven)";

    public static async Task<int> RunAsync(string runsDirectory, string evaluationPath, string integrityRawPath, string controlsPath, string anchorsPath,
        IReadOnlyList<string> identityPaths, IReadOnlyList<string> definitionPaths, string fixtureReportPath, string referenceSumsPath, string runnerDiffPath,
        string summaryPath, string? environmentPath)
    {
        var reasons = new List<string>();
        var stage = StageFolder.Load(runsDirectory);
        var plan = RulesPlan.Load(runsDirectory);
        var raw = Files.ReadIntegrity(integrityRawPath);
        var evaluationNode = JsonNode.Parse(File.ReadAllText(evaluationPath))!;
        var ev = EvaluateCommand.Read(evaluationPath).ToDictionary(e => e.Run, StringComparer.Ordinal);
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

        if (definitionPaths.Count != 2 || !definitionPaths.All(File.Exists)
            || File.ReadAllText(definitionPaths[0]).Trim() != File.ReadAllText(definitionPaths[1]).Trim() || File.ReadAllText(definitionPaths[0]).Trim().Length == 0)
        {
            reasons.Add("the observation definitions changed after the window request (or a definitions file is missing)");
        }

        if (fixtureReport["selfCheck"] is null || fixtureReport["secondBuild"]?.GetValue<string>() != "identical")
        {
            reasons.Add("the fixture builder's self-check or second build did not pass");
        }

        var builtSums = fixtureReport["sumsSha256"]?.GetValue<string>();
        if (evaluationNode["checker"]?["fixtureSumsSha256"]?.GetValue<string>() != builtSums)
        {
            reasons.Add("the checked fixtures are not the built ones (SHA256SUMS differs)");
        }

        if (builtSums != HashFile(referenceSumsPath))
        {
            reasons.Add("the built SHA256SUMS differs from the committed one of spikes/12-group-continuity");
        }

        var planned = plan.Runs;
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

            if (!ev.ContainsKey(p.RunId) || !raw.ContainsKey(p.RunId))
            {
                reasons.Add($"{p.RunId}: a check entry is missing (evaluation {ev.ContainsKey(p.RunId)}, raw text {raw.ContainsKey(p.RunId)})");
            }
        }

        var anchorRuns = planned.Count(p => p.Anchor is not null);
        if (anchors["runsCompared"]?.GetValue<int>() != anchorRuns || anchors["differences"]?.GetValue<int>() != 0)
        {
            reasons.Add($"anchors: {anchors["runsCompared"]} of {anchorRuns} compared, {anchors["differences"]} differences");
        }

        SameBody(planned, ev, reasons);

        var trusted = controls["trusted"]?.GetValue<bool>() == true;
        if (!trusted)
        {
            reasons.Add("the instrument is not trusted: " + string.Join("; ", controls["items"]!.AsArray()
                .Where(i => i!["behaved"]!.GetValue<string>() == "no").Select(i => $"{i!["group"]} {i["id"]} on {i["run"]}")));
        }

        foreach (var set in planned.Where(p => p.Mutation is null && p.FooterOn).GroupBy(p => (p.FixtureId, p.Configuration)).Where(g => g.Count() > 1))
        {
            var checkedRuns = set.Select(p => ev.GetValueOrDefault(p.RunId)).Where(e => e is { Verdict: "checked" }).Select(e => e!).ToList();
            if (checkedRuns.Select(e => (e.Accounting.Pages, e.Accounting.RowSetsSha256, e.Text?.ContentSha256)).Distinct().Count() > 1)
            {
                reasons.Add($"{set.Key.FixtureId} {set.Key.Configuration}: the repetitions differ");
            }
        }

        foreach (var e in ev.Values)
        {
            var a = e.Accounting;
            if (a.RunClass == "error")
            {
                reasons.Add($"{e.Run} ended in error: {a.RunReason}");
            }

            if (a.RunClass == "ok" && a.SemPass != true)
            {
                reasons.Add($"{e.Run}: the semantic-source check failed");
            }

            if (e.Verdict == "checked" && a.Rules.FirstOrDefault(r => r.Rule == 8)?.Outcome != "PASS")
            {
                reasons.Add($"{e.Run}: escaping and isolation failed");
            }

            if (e.Groups?.FixtureTotalDefects.Count > 0)
            {
                reasons.Add($"{e.Run}: fixture defect — {string.Join("; ", e.Groups.FixtureTotalDefects)}");
            }
        }

        RunnerDiff(runnerDiffPath, reasons);

        // A planned run without an evaluation entry is already a reason; it is kept as an unchecked entry so the results can still be written.
        foreach (var p in planned.Where(p => !ev.ContainsKey(p.RunId)))
        {
            ev[p.RunId] = new EvaluatedEntry { Run = p.RunId, FixtureId = p.FixtureId, Configuration = p.Configuration, Verdict = "no-check", Reason = "no evaluation entry" };
            ev[p.RunId].Accounting.RunClass = "missing";
        }

        var outcomes = planned.ToDictionary(p => p.RunId, p => Outcome(ev[p.RunId]), StringComparer.Ordinal);
        var summary = new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["environment"] = environmentPath is not null && File.Exists(environmentPath) ? JsonNode.Parse(File.ReadAllText(environmentPath)) : null,
            ["checker"] = evaluationNode["checker"]?.DeepClone(),
            ["fixtures"] = fixtureReport.DeepClone(),
            ["verdict"] = new JsonObject { ["exitCode"] = reasons.Count == 0 ? 0 : 1, ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)r)]) },
            ["controls"] = controls.DeepClone(),
            ["anchors"] = new JsonObject { ["runsCompared"] = anchors["runsCompared"]?.DeepClone(), ["differences"] = anchors["differences"]?.DeepClone() },
            ["integrity"] = trusted ? Integrity(planned, ev, outcomes) : null,
            ["behaviours"] = trusted ? Behaviours(planned, ev, outcomes) : null,
            ["costs"] = trusted ? Costs(planned, ev, anchors) : null,
            ["interactions"] = trusted ? Interactions(planned, ev) : null,
            ["counts"] = new JsonObject
            {
                ["planned"] = planned.Count,
                ["classes"] = JsonSerializer.SerializeToNode(ev.Values.GroupBy(e => e.Accounting.RunClass).ToDictionary(x => x.Key, x => x.Count())),
                ["outcomes"] = trusted ? JsonSerializer.SerializeToNode(outcomes.Values.GroupBy(o => o.Outcome).ToDictionary(x => x.Key, x => x.Count())) : null,
                ["unlistedTextFailureRuns"] = outcomes.Values.Count(o => o.Surplus > 0),
                ["repeatedHeaderLinesRemoved"] = ev.Values.Sum(e => e.Removed.Count),
            },
            ["outcomes"] = trusted ? new JsonArray([.. planned.Select(p => (JsonNode)new JsonObject
            {
                ["run"] = p.RunId,
                ["configuration"] = p.Configuration,
                ["fixture"] = p.FixtureId,
                ["row"] = p.MatrixRow,
                ["footerOn"] = p.FooterOn,
                ["outcome"] = outcomes[p.RunId].Outcome,
                ["refuting"] = new JsonArray([.. outcomes[p.RunId].Refuting.Select(x => (JsonNode)x)]),
                ["partial"] = new JsonArray([.. outcomes[p.RunId].Partial.Select(x => (JsonNode)x)]),
                ["unlistedTextFailures"] = outcomes[p.RunId].Surplus,
            })]) : null,
        };
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(StageFolder.Json) + "\n");
        foreach (var r in reasons)
        {
            Console.WriteLine("REASON: " + r);
        }

        Console.WriteLine($"verdict exit {(reasons.Count == 0 ? 0 : 1)}; instrument trusted {trusted}.");
        return reasons.Count == 0 ? 0 : 1;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Every behaviour run's body equals the run without behaviours of the same fixture, footer and repetition; its page grows by one constant length per configuration.</summary>
    private static void SameBody(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, List<string> reasons)
    {
        var growth = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var p in planned.Where(p => p.Configuration != Configurations.None && p.Mutation is null))
        {
            var mine = ev.GetValueOrDefault(p.RunId)?.Accounting;
            var none = Counterpart(planned, ev, p, Configurations.None)?.Accounting;
            if (mine is not { RunClass: "ok" } || none is not { RunClass: "ok" })
            {
                continue;
            }

            if (mine.SemInnerSha256 != none.SemInnerSha256)
            {
                reasons.Add($"{p.RunId}: its body differs from the run without behaviours");
            }

            if (mine.HtmlBytes is { } m && none.HtmlBytes is { } n)
            {
                if (!growth.TryGetValue(p.Configuration, out var set))
                {
                    set = [];
                    growth[p.Configuration] = set;
                }

                set.Add(m - n);
            }
        }

        foreach (var (configuration, set) in growth)
        {
            if (set.Count != 1 || set.Single() <= 0)
            {
                reasons.Add($"{configuration}: the PDF page grows by {string.Join(", ", set)} bytes (one positive length expected)");
            }
        }
    }

    /// <summary>The runner's copy changes only the script count: the signature and three uses, plus the header comment.</summary>
    private static void RunnerDiff(string path, List<string> reasons)
    {
        string[] lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var removed = lines.Where(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal)).ToList();
        var added = lines.Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)).ToList();
        var addedCode = added.Where(l => !l[1..].TrimStart().StartsWith("///", StringComparison.Ordinal)).ToList();
        var ok = removed.Count == 4 && addedCode.Count == 4 && added.Count == 8
            && removed.All(l => l.Contains("PageBuilder.PdfScripts", StringComparison.Ordinal) || l.Contains("CancellationToken cancellation)", StringComparison.Ordinal))
            && addedCode.All(l => l.Contains("pdfScripts", StringComparison.Ordinal));
        if (!ok)
        {
            reasons.Add($"the runner's copy differs beyond the declared change ({removed.Count} lines removed, {added.Count} added)");
        }
    }

    private static EvaluatedEntry? Counterpart(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, RulesPlanned p, string configuration)
    {
        var other = planned.FirstOrDefault(o => o.Configuration == configuration && o.FixtureId == p.FixtureId && o.FooterOn == p.FooterOn
            && o.Repetition == p.Repetition && o.Mutation == p.Mutation);
        return other is null ? null : ev.GetValueOrDefault(other.RunId);
    }

    internal sealed record RunOutcome(string Outcome, List<string> Refuting, List<string> Partial, int Surplus);

    /// <summary>A run's outcome with its causes (the rules of spikes/12-group-continuity, on the reading); controls carry "control".</summary>
    public static RunOutcome Outcome(EvaluatedEntry ev)
    {
        var e = ev.Accounting;
        var refuting = new List<string>();
        var partial = new List<string>();
        if (e.RunClass != "ok")
        {
            return new RunOutcome($"inconclusive ({e.RunClass})", refuting, partial, 0);
        }

        var ti = ev.Text;
        if (ev.Verdict != "checked" || ev.Groups is null || ti is null || ti.Verdict is not ("pass" or "fail"))
        {
            return new RunOutcome($"inconclusive (check {ev.Verdict}/{ti?.Verdict ?? "missing"})", refuting, partial, 0);
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

        foreach (var f in ev.Groups.Failures)
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
            return new RunOutcome("control", refuting, partial, surplus);
        }

        var outcome = refuting.Count > 0 ? "refuted"
            : surplus > 0 ? SurplusOnly
            : partial.Count > 0 ? "partial"
            : "confirmed";
        return new RunOutcome(outcome, refuting, partial, surplus);
    }

    public static string Case(string fixture) => fixture switch
    {
        _ when fixture.StartsWith("GE", StringComparison.Ordinal) => "GE — a group starting near the end of a page",
        _ when fixture.StartsWith("GS", StringComparison.Ordinal) => "GS — a group spanning several or many pages",
        "GTL300u" or "GTF300u" => "GT — an over-tall row as a group's last or first row",
        "T300u" or "X64K" or "X1M" or "G200" => "GI — over-tall rows inside a group",
        "B100" or "B1000" or "B4000" => "ordinary blocks",
        _ => "controls",
    };

    /// <summary>S1: per configuration and case, the outcomes, the group rules and the page accounting.</summary>
    private static JsonObject Integrity(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, Dictionary<string, RunOutcome> outcomes)
    {
        var result = new JsonObject();
        foreach (var configuration in Configurations.All)
        {
            var byConfiguration = new JsonObject();
            foreach (var byCase in planned.Where(p => p.Configuration == configuration && p.Mutation is null).GroupBy(p => Case(p.FixtureId)))
            {
                var ok = byCase.Where(p => ev[p.RunId].Verdict == "checked").ToList();
                JsonObject Rule(string rule) => new()
                {
                    ["statement"] = ok.All(p => ev[p.RunId].Groups!.Failures.All(f => f.Rule != rule)) ? "confirmed" : "refuted",
                    ["failing"] = new JsonArray([.. ok.Where(p => ev[p.RunId].Groups!.Failures.Any(f => f.Rule == rule)).Select(p => (JsonNode)p.RunId)]),
                };
                var footerOn = ok.Where(p => p.FooterOn).ToList();
                byConfiguration[byCase.Key] = new JsonObject
                {
                    ["runs"] = byCase.Count(),
                    ["runsChecked"] = ok.Count,
                    ["text"] = new JsonObject
                    {
                        ["pass"] = ok.Count(p => ev[p.RunId].Text?.Verdict == "pass"),
                        ["failing"] = new JsonArray([.. ok.Where(p => ev[p.RunId].Text?.Verdict != "pass").Select(p => (JsonNode)p.RunId)]),
                    },
                    ["headers"] = Rule("GC-1"),
                    ["groupSubtotalsStaticText"] = Rule("GC-2"),
                    ["membership"] = Rule("GC-3"),
                    ["blockTotal"] = Rule("GC-4"),
                    ["pageAccounting"] = new JsonObject
                    {
                        ["statement"] = footerOn.All(p => ev[p.RunId].Accounting.AccountingVerdict == "exact") ? "exact" : "not exact on every run",
                        ["footerOnRuns"] = footerOn.Count,
                        ["inexact"] = new JsonArray([.. footerOn.Where(p => ev[p.RunId].Accounting.AccountingVerdict != "exact").Select(p => (JsonNode)$"{p.RunId}: {ev[p.RunId].Accounting.AccountingVerdict}")]),
                    },
                    ["rule7"] = new JsonArray([.. ok.Where(p => ev[p.RunId].Accounting.Rule7 is not null).Select(p => (JsonNode)$"{p.RunId}: {ev[p.RunId].Accounting.Rule7!.Outcome}")]),
                    ["outcomes"] = JsonSerializer.SerializeToNode(byCase.GroupBy(p => outcomes[p.RunId].Outcome).ToDictionary(x => x.Key, x => x.Count())),
                };
            }

            result[configuration] = byConfiguration;
        }

        result["note"] = "On the reading (repeated-header lines set aside by their exact form). The group-total text is static text written from the fixture: the group-subtotal statement shows whether it survives pagination; the page subtotals and the chain are what the accounting script computes after layout.";
        return result;
    }

    /// <summary>S2: each behaviour, in its own configuration and with all three, per case and over all cases — counts and the class.</summary>
    private static JsonObject Behaviours(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, Dictionary<string, RunOutcome> outcomes)
    {
        var result = new JsonObject();
        foreach (var behaviour in Configurations.Behaviours)
        {
            var byConfiguration = new JsonObject();
            foreach (var configuration in new[] { Configurations.None, behaviour, Configurations.AllThree })
            {
                var runs = planned.Where(p => p.Configuration == configuration && p.Mutation is null).ToList();
                var byCase = new JsonObject();
                foreach (var group in runs.GroupBy(p => Case(p.FixtureId)))
                {
                    byCase[group.Key] = Classify(behaviour, configuration, [.. group], planned, ev, outcomes);
                }

                byCase["all cases"] = Classify(behaviour, configuration, runs, planned, ev, outcomes);
                byConfiguration[configuration] = byCase;
            }

            result[behaviour] = byConfiguration;
        }

        result["note"] = "Each class describes what these runs printed with this mechanism, these fixtures, this Paged.js and this Chromium; none is a judgement against a product acceptance. The configuration without behaviours is shown for reference and is not classed.";
        return result;
    }

    private static JsonObject Classify(string behaviour, string configuration, List<RulesPlanned> runs, List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, Dictionary<string, RunOutcome> outcomes)
    {
        var instances = 0;
        var produced = 0;
        var violations = new JsonArray();
        var spurious = new JsonArray();
        var notOk = new JsonArray();
        var notConfirmed = new JsonArray();
        var contested = 0;
        var contestedProduced = 0;
        var linesOnInstancePages = 0;
        var okRuns = 0;
        foreach (var p in runs)
        {
            var e = ev[p.RunId];
            if (e.Verdict != "checked" || e.Behaviours is null)
            {
                notOk.Add($"{p.RunId}: {e.Accounting.RunClass}{(e.Reason is null ? string.Empty : $" ({e.Reason})")}");
                continue;
            }

            okRuns++;
            if (outcomes[p.RunId].Outcome != "confirmed")
            {
                notConfirmed.Add($"{p.RunId}: {outcomes[p.RunId].Outcome} — {string.Join("; ", outcomes[p.RunId].Refuting.Concat(outcomes[p.RunId].Partial).Take(5))}");
            }

            var count = e.Behaviours.Of(behaviour);
            instances += count.Instances;
            produced += count.Produced;
            foreach (var v in count.Violations)
            {
                violations.Add($"{p.FixtureId}{(p.FooterOn ? string.Empty : " (footer off)")}{(p.Repetition > 1 ? $" r{p.Repetition}" : string.Empty)}: {v}");
            }

            foreach (var s in count.Spurious)
            {
                spurious.Add($"{p.FixtureId}: {s}");
            }

            if (behaviour == Configurations.RepeatHeader)
            {
                contested += count.Instances;
                contestedProduced += count.Produced;
                linesOnInstancePages += e.Removed.Count - count.Spurious.Count;
            }
            else if (Counterpart(planned, ev, p, Configurations.None)?.Behaviours?.Of(behaviour) is { } baseline)
            {
                contested += baseline.ViolatedInstances.Count;
                contestedProduced += baseline.ViolatedInstances.Count(count.ProducedInstances.Contains);
            }
        }

        string cls;
        if (configuration == Configurations.None)
        {
            cls = "reference (not attempted)";
        }
        else if (okRuns == runs.Count && notConfirmed.Count == 0 && violations.Count == 0 && spurious.Count == 0)
        {
            cls = "produced";
        }
        else if (okRuns == 0
            || (behaviour == Configurations.RepeatHeader ? linesOnInstancePages == 0 : contested > 0 && contestedProduced == 0))
        {
            cls = "not produced";
        }
        else
        {
            cls = "partly produced";
        }

        return new JsonObject
        {
            ["class"] = cls,
            ["runs"] = runs.Count,
            ["runsChecked"] = okRuns,
            ["instances"] = instances,
            ["produced"] = produced,
            ["violations"] = violations,
            ["spurious"] = spurious,
            ["contestedInstances"] = contested,
            ["contestedProduced"] = contestedProduced,
            ["runsNotOk"] = notOk,
            ["runsNotConfirmed"] = notConfirmed,
        };
    }

    /// <summary>S3: every behaviour run against the run without behaviours of the same fixture, footer and repetition; medians of the repeated fixtures.</summary>
    private static JsonObject Costs(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev, JsonNode anchors)
    {
        var reference = anchors["runs"]!.AsArray().ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!["referenceMeasurements"], StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var p in planned.Where(p => p.Mutation is null))
        {
            var e = ev[p.RunId].Accounting;
            if (e.RunClass != "ok")
            {
                continue;
            }

            var none = Counterpart(planned, ev, p, Configurations.None)?.Accounting;
            var noneId = planned.First(o => o.Configuration == Configurations.None && o.FixtureId == p.FixtureId && o.FooterOn == p.FooterOn && o.Repetition == p.Repetition && o.Mutation is null).RunId;
            rows.Add(new JsonObject
            {
                ["run"] = p.RunId,
                ["configuration"] = p.Configuration,
                ["fixture"] = p.FixtureId,
                ["footerOn"] = p.FooterOn,
                ["repetition"] = p.Repetition,
                ["pages"] = e.Pages,
                ["pagesWithout"] = none?.Pages,
                ["pagesDifference"] = e.Pages - none?.Pages,
                ["layoutMs"] = Value(e, "layoutMs"),
                ["layoutRatio"] = Ratio(e, none, "layoutMs"),
                ["pagedFlowMs"] = Value(e, "pagedFlowMs"),
                ["pagedFlowRatio"] = Ratio(e, none, "pagedFlowMs"),
                ["totalMs"] = Value(e, "totalMs"),
                ["totalRatio"] = Ratio(e, none, "totalMs"),
                ["chromiumTreePeakRssKiB"] = Value(e, "chromiumTreePeakRssKiB"),
                ["chromiumPeakDifferenceKiB"] = Value(e, "chromiumTreePeakRssKiB") - (none is null ? null : Value(none, "chromiumTreePeakRssKiB")),
                ["containerRunningPeakBytes"] = e.Measurements["cgroupMemoryPeakBytes"]?.DeepClone(),
                ["referenceWithout"] = reference.GetValueOrDefault(noneId)?.DeepClone(),
            });
        }

        var medians = new JsonArray();
        foreach (var set in planned.Where(p => p.Mutation is null && p.FooterOn).GroupBy(p => (p.FixtureId, p.Configuration)).Where(g => g.Count() >= 3))
        {
            var ok = set.Select(p => ev[p.RunId].Accounting).Where(e => e.RunClass == "ok").ToList();
            medians.Add(new JsonObject
            {
                ["fixture"] = set.Key.FixtureId,
                ["configuration"] = set.Key.Configuration,
                ["runs"] = ok.Count,
                ["pages"] = ok.FirstOrDefault()?.Pages,
                ["layoutMsMedian"] = Median(ok.Select(e => Value(e, "layoutMs"))),
                ["pagedFlowMsMedian"] = Median(ok.Select(e => Value(e, "pagedFlowMs"))),
                ["totalMsMedian"] = Median(ok.Select(e => Value(e, "totalMs"))),
                ["chromiumTreePeakRssKiBMedian"] = Median(ok.Select(e => Value(e, "chromiumTreePeakRssKiB"))),
            });
        }

        return new JsonObject
        {
            ["runs"] = rows,
            ["medians"] = medians,
            ["note"] = "The container running peak is the stage container's running maximum up to that run, not a per-run figure: listed, never compared. The values of spikes/12-group-continuity for the same runs without behaviours are set beside as reference.",
        };

        static double? Value(StrictEntry e, string name) => e.Measurements[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

        static double? Ratio(StrictEntry e, StrictEntry? none, string name) =>
            none is not null && Value(e, name) is { } a && Value(none, name) is { } b && b > 0 ? Math.Round(a / b, 3) : null;

        static double? Median(IEnumerable<double?> values)
        {
            var list = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
            return list.Count == 0 ? null : list.Count % 2 == 1 ? list[list.Count / 2] : (list[(list.Count / 2) - 1] + list[list.Count / 2]) / 2;
        }
    }

    /// <summary>S4: where all three together differ from each behaviour alone (instances produced, pages).</summary>
    private static JsonArray Interactions(List<RulesPlanned> planned, Dictionary<string, EvaluatedEntry> ev)
    {
        var rows = new JsonArray();
        foreach (var p in planned.Where(p => p.Configuration == Configurations.AllThree && p.Mutation is null))
        {
            var all = ev[p.RunId];
            if (all.Behaviours is null)
            {
                continue;
            }

            var differences = new JsonArray();
            foreach (var behaviour in Configurations.Behaviours)
            {
                var alone = Counterpart(planned, ev, p, behaviour);
                if (alone?.Behaviours is null)
                {
                    continue;
                }

                var together = all.Behaviours.Of(behaviour).ProducedInstances.ToHashSet(StringComparer.Ordinal);
                var single = alone.Behaviours.Of(behaviour).ProducedInstances.ToHashSet(StringComparer.Ordinal);
                var lost = single.Except(together).Order(StringComparer.Ordinal).ToList();
                var gained = together.Except(single).Order(StringComparer.Ordinal).ToList();
                if (lost.Count > 0 || gained.Count > 0 || all.Behaviours.Of(behaviour).Spurious.Count != alone.Behaviours.Of(behaviour).Spurious.Count)
                {
                    differences.Add($"{behaviour}: produced alone only {string.Join(",", lost)}; produced together only {string.Join(",", gained)}; spurious together {all.Behaviours.Of(behaviour).Spurious.Count}, alone {alone.Behaviours.Of(behaviour).Spurious.Count}");
                }

                if (alone.Accounting.Pages != all.Accounting.Pages)
                {
                    differences.Add($"pages: all three {all.Accounting.Pages}, {behaviour} alone {alone.Accounting.Pages}");
                }
            }

            if (differences.Count > 0)
            {
                rows.Add(new JsonObject { ["run"] = p.RunId, ["differences"] = differences });
            }
        }

        return rows;
    }
}
