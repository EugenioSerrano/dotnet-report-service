using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using StrictAccountingCheck;
using TextIntegrityCheck;

namespace GroupContinuityCheck;

/// <summary>
/// The "parity" command: the runs this experiment shares with spikes/12-group-continuity
/// (laid out here with this folder's derived accounting script and page builder, without
/// an opening amount) against that folder's committed results, field by field: class and
/// pages; text verdict and content hash; accounting verdict and row-set hash; the group
/// check's failures and its placement counts. Every difference is listed.
/// </summary>
internal static class ParityCommand
{
    public static async Task<int> RunAsync(Dictionary<string, string> o, int timeoutSeconds, CancellationToken cancellation)
    {
        var reference = o["reference"];
        var csv = File.ReadAllLines(Path.Combine(reference, "runs.csv"));
        var header = csv[0].Split(',');
        var refRuns = csv.Skip(1).Select(l => l.Split(',')).ToDictionary(c => c[0], c => header.Zip(c).ToDictionary(x => x.First, x => x.Second), StringComparer.Ordinal);
        var refTi = Files.ReadIntegrity(Path.Combine(reference, "integrity.json"));
        var refAccounting = JsonNode.Parse(File.ReadAllText(Path.Combine(reference, "accounting.json")))!["runs"]!.AsArray().ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var refGroups = JsonNode.Parse(File.ReadAllText(Path.Combine(reference, "groups.json")))!["runs"]!.AsArray().ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var stage = StageFolder.Load(o["runs"]);
        var ti = Files.ReadIntegrity(o["integrity"]);
        var accounting = JsonNode.Parse(File.ReadAllText(o["accounting"]))!["runs"]!.AsArray().ToDictionary(r => r!["run"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var sums = Sums.Read(Path.Combine(o["fixtures"], FixtureFile.SumsFileName));
        var rows = new JsonArray();
        var differences = 0;
        foreach (var id in ReportControls.ParityRuns)
        {
            var listed = new JsonArray();
            var row = new JsonObject { ["run"] = id };
            void Field(string name, object? mine, object? theirs)
            {
                var a = Convert.ToString(mine, CultureInfo.InvariantCulture);
                var b = Convert.ToString(theirs, CultureInfo.InvariantCulture);
                row[name] = new JsonObject { ["here"] = a, ["folder12"] = b };
                if (a != b)
                {
                    listed.Add($"{name}: {a} here, {b} in spikes/12-group-continuity");
                }
            }

            var run = stage.Runs.GetValueOrDefault(id);
            var r = refRuns.GetValueOrDefault(id);
            Field("class", run?.Outcome, r?.GetValueOrDefault("outcome"));
            Field("pages", run?.Pages, r?.GetValueOrDefault("pages"));
            Field("textVerdict", ti.GetValueOrDefault(id)?.Verdict, refTi.GetValueOrDefault(id)?.Verdict);
            Field("textContentSha256", ti.GetValueOrDefault(id)?.ContentSha256, refTi.GetValueOrDefault(id)?.ContentSha256);
            Field("accountingVerdict", accounting.GetValueOrDefault(id)?["accountingVerdict"]?.GetValue<string>(), refAccounting.GetValueOrDefault(id)?["accountingVerdict"]?.GetValue<string>());
            Field("rowSetsSha256", accounting.GetValueOrDefault(id)?["rowSetsSha256"]?.GetValue<string>(), refAccounting.GetValueOrDefault(id)?["rowSetsSha256"]?.GetValue<string>());
            var theirs = refGroups.GetValueOrDefault(id)?["result"];
            GroupResult? mine = null;
            if (run is { Outcome: "ok", Pdf: not null })
            {
                var fixture = FixtureFile.ReadVerified(o["fixtures"], run.FixtureId, sums);
                var reading = await Account.ReadAsync(Path.Combine(stage.Directory, run.Pdf), fixture, true, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
                mine = GroupCheck.Check(reading, fixture);
            }

            Field("groupFailures", mine is null ? null : string.Join(" ", mine.Failures.Select(f => f.Key)),
                theirs?["failures"] is JsonArray tf ? string.Join(" ", tf.Select(f => $"{f!["rule"]}|{f["class"]}|{f["group"]?.GetValue<string>() ?? f["row"]?.ToString() ?? "-"}")) : null);
            Field("headersAlone", mine?.HeadersAlone, theirs?["headersAlone"]?.GetValue<int>());
            Field("headersRepeated", mine?.HeadersRepeated, theirs?["headersRepeated"]?.GetValue<int>());
            Field("continuationPagesWithoutHeader", mine?.ContinuationPagesWithoutHeader, theirs?["continuationPagesWithoutHeader"]?.GetValue<int>());
            Field("totalsSeparated", mine?.TotalsSeparated, theirs?["totalsSeparated"]?.GetValue<int>());
            row["differences"] = listed;
            differences += listed.Count;
            rows.Add(row);
            Console.WriteLine($"{id}: {(listed.Count == 0 ? "no difference" : string.Join("; ", listed.Select(x => x!.GetValue<string>())))}");
        }

        PartFixtures.WriteJson(o["out"], new JsonObject { ["compared"] = rows.Count, ["differences"] = differences, ["runs"] = rows });
        Console.WriteLine($"Parity: {rows.Count} runs, {differences} differences.");
        return 0;
    }
}

/// <summary>
/// The "verdict" command: the exit code (instrument health only), the outcome of every
/// assembled report, the statements per case, the boundary observations counted per case,
/// reproducibility, and the costs, into summary.json.
/// </summary>
internal static class Verdict
{
    private static readonly Dictionary<string, string> Cases = new(StringComparer.Ordinal)
    {
        ["XB-L180-H1"] = "4 near a group's end",
        ["XB-L181-H1"] = "4 near a group's end",
        ["XB-L182-H1"] = "4 between groups",
        ["XB-L183-H1"] = "4 near a group's start",
        ["XB-L184-H1"] = "4 near a group's start",
        ["XB-L185-H1"] = "4 near a group's start",
        ["XB-L201-H1"] = "5 one-record group at a part end",
        ["XB-TL150-H1"] = "6 over-tall rows at the boundary",
        ["XB-TF150-H1"] = "6 over-tall rows at the boundary",
        ["XB-W250-H1"] = "7 groups spanning whole blocks",
        ["XB-G500-H1"] = "7 groups spanning whole blocks",
        ["XB-PB-H1"] = "8 boundary on a page boundary",
        ["XB-ALL-H1"] = "11 top of the bracket",
        ["XB-L185-H0"] = "9 baseline: independent parts",
        ["XB-W250-H0"] = "9 baseline: independent parts",
    };

    public static async Task<int> RunAsync(Dictionary<string, string> o)
    {
        var reasons = new List<string>();
        var summary = new JsonObject();

        // Identity at the start and the end; the fixture pin; the builder's self-checks; the calibration.
        foreach (var path in o["identity"].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList() : [];
            var bad = lines.Count(l => l.Split(' ') is var p && (p.Length < 3 || p[0] != p[1]));
            if (lines.Count == 0 || bad > 0)
            {
                reasons.Add($"identity {Path.GetFileName(path)}: {lines.Count} lines, {bad} mismatches");
            }
        }

        var fixtures = o["fixtures"];
        var sums = Sums.Read(Path.Combine(fixtures, FixtureFile.SumsFileName));
        var badFixtures = sums.Count(kv => !File.Exists(Path.Combine(fixtures, kv.Key)) || Sums.HashFile(Path.Combine(fixtures, kv.Key)) != kv.Value);
        if (badFixtures > 0)
        {
            reasons.Add($"{badFixtures} fixture files do not match SHA256SUMS");
        }

        var fixtureReport = JsonNode.Parse(File.ReadAllText(o["fixture-report"]))!;
        if (fixtureReport["selfChecks"] is not JsonArray { Count: 4 })
        {
            reasons.Add("the fixture builder's self-checks are not recorded");
        }

        var calibration = JsonNode.Parse(File.ReadAllText(o["calibration"]))!;
        var limit = calibration["blockLimit"]?.GetValue<int?>();
        var pbLimit = fixtureReport["reports"]!.AsArray().FirstOrDefault(r => r!["report"]!.GetValue<string>() == "XB-PB-H1")?["blockLimit"]?.GetValue<int>();
        if (limit is null || pbLimit != limit)
        {
            reasons.Add($"calibration block limit {limit} against the page-coinciding report's {pbLimit}");
        }

        // Every planned run has its record and entries; every instance its assembly, verify, slices and report entries.
        var stage = StageFolder.Load(o["runs"]);
        var ti = Files.ReadIntegrity(o["integrity"]);
        var sliceTi = Files.ReadIntegrity(o["integrity-slices"]);
        var accounting = (JsonNode.Parse(File.ReadAllText(o["accounting"]))!["runs"]!.Deserialize<List<StrictEntry>>(StageFolder.Json) ?? []).ToDictionary(e => e.Run, StringComparer.Ordinal);
        foreach (var p in stage.Plan.Runs)
        {
            if (stage.RecordCounts.GetValueOrDefault(p.RunId) != 1 || !ti.ContainsKey(p.RunId) || !accounting.ContainsKey(p.RunId))
            {
                reasons.Add($"{p.RunId}: {stage.RecordCounts.GetValueOrDefault(p.RunId)} records, text entry {ti.ContainsKey(p.RunId)}, accounting entry {accounting.ContainsKey(p.RunId)}");
            }
        }

        if (stage.Plan.Runs.Select(p => p.RunId).Distinct(StringComparer.Ordinal).Count() != stage.Plan.Runs.Count)
        {
            reasons.Add("duplicate run ids in the plan");
        }

        var errors = stage.Runs.Values.Where(r => r.Outcome == "error").Select(r => r.RunId).ToList();
        if (errors.Count > 0)
        {
            reasons.Add($"runs with class error: {string.Join(" ", errors)}");
        }

        foreach (var r in stage.Runs.Values.Where(r => r.Outcome == "ok"))
        {
            var e = accounting.GetValueOrDefault(r.RunId);
            var escaping = e?.Rules.FirstOrDefault(x => x.Rule == 8);
            if (!r.SemPass || escaping?.Outcome != "PASS")
            {
                reasons.Add($"{r.RunId}: SEM {(r.SemPass ? "pass" : "FAIL")}, escaping {escaping?.Outcome ?? "missing"}");
            }
        }

        var instances = Instances.Read(o["reports"]);
        var report = JsonNode.Parse(File.ReadAllText(o["report"]))!["instances"]!.AsArray().ToDictionary(n => n!["instance"]!.GetValue<string>(), n => n!, StringComparer.Ordinal);
        var observations = JsonNode.Parse(File.ReadAllText(o["observations"]))!["instances"]!.AsArray().ToDictionary(n => n!["instance"]!.GetValue<string>(), n => n!, StringComparer.Ordinal);
        foreach (var i in instances.Where(i => i.Complete))
        {
            var n = report.GetValueOrDefault(i.Instance);
            var sliceCount = Enumerable.Range(1, i.Parts.Count).Count(k => sliceTi.ContainsKey(Instances.SliceId(i.Instance, k)));
            if (n?["verdict"]?.GetValue<string>() != "checked" || n["verifyNumbered"]?["exists"]?.GetValue<bool>() != true || n["verifyUnnumbered"]?["exists"]?.GetValue<bool>() != true
                || sliceCount != i.Parts.Count || !observations.ContainsKey(i.Instance))
            {
                reasons.Add($"{i.Instance}: report {n?["verdict"]}, verify entries, {sliceCount}/{i.Parts.Count} slices checked, observations {observations.ContainsKey(i.Instance)}");
            }
        }

        // The instrument, parity, reproducibility.
        var controls = JsonNode.Parse(File.ReadAllText(o["controls"]))!;
        var trusted = controls["trusted"]!.GetValue<bool>();
        if (!trusted)
        {
            reasons.Add("the instrument is not trusted");
        }

        var parity = JsonNode.Parse(File.ReadAllText(o["parity"]))!;
        if (parity["differences"]!.GetValue<int>() != 0 || parity["compared"]!.GetValue<int>() != ReportControls.ParityRuns.Length)
        {
            reasons.Add($"parity with spikes/12-group-continuity: {parity["differences"]} differences over {parity["compared"]} runs");
        }

        var repro = Reproducibility(instances, report, accounting, ti);
        if (!repro["agree"]!.GetValue<bool>())
        {
            reasons.Add("the repetitions of the whole-block report disagree");
        }

        // Outcomes (only once the instrument is trusted; otherwise recorded as unread).
        var outcomes = new JsonArray();
        foreach (var i in instances)
        {
            outcomes.Add(Outcome(i, report.GetValueOrDefault(i.Instance), accounting, sliceTi, stage, trusted));
        }

        summary["verdict"] = new JsonObject { ["exitCode"] = reasons.Count == 0 ? 0 : 1, ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)r)]) };
        summary["trusted"] = trusted;
        summary["planned"] = stage.Plan.Runs.Count;
        summary["classes"] = JsonSerializer.SerializeToNode(stage.Runs.Values.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()));
        summary["outcomes"] = outcomes;
        summary["statements"] = trusted ? Statements(outcomes, observations) : "not read: the instrument is not trusted";
        summary["parity"] = parity;
        summary["reproducibility"] = repro;
        summary["costs"] = Costs(stage, instances, report);
        summary["calibration"] = calibration.DeepClone();
        summary["handoverComputeMs"] = fixtureReport["handoverComputeMs"]?.DeepClone();
        PartFixtures.WriteJson(o["summary"], summary);
        foreach (var r in reasons)
        {
            Console.WriteLine("REASON: " + r);
        }

        Console.WriteLine($"Verdict: exit {(reasons.Count == 0 ? 0 : 1)}; trusted {trusted}; outcomes " + string.Join(", ", outcomes.GroupBy(x => x!["outcome"]!.GetValue<string>()).Select(g => $"{g.Count()} {g.Key}")));
        await Task.CompletedTask;
        return reasons.Count == 0 ? 0 : 1;
    }

    private static JsonObject Outcome(ReportInstance i, JsonNode? n, Dictionary<string, StrictEntry> accounting, Dictionary<string, IntegrityEntry> sliceTi, StageFolder stage, bool trusted)
    {
        var node = new JsonObject { ["instance"] = i.Instance, ["report"] = i.Report, ["case"] = Cases.GetValueOrDefault(i.Report, "probe"), ["variant"] = i.Variant };
        var causes = new JsonArray();
        string outcome;
        if (!i.Complete || n?["verdict"]?.GetValue<string>() != "checked" || n["verifyNumbered"]?["exists"]?.GetValue<bool>() != true)
        {
            outcome = $"inconclusive ({i.Reason ?? n?["verdict"]?.GetValue<string>() ?? "no report entry"})";
        }
        else if (!trusted)
        {
            outcome = "not read";
        }
        else
        {
            var refuting = new List<string>();
            var partial = new List<string>();
            refuting.AddRange(n["accountingFailures"]!.AsArray().Select(x => x!.GetValue<string>()));
            refuting.AddRange(n["groupFailures"]!.AsArray().Select(x => $"{x!["rule"]}|{x["class"]}|{x["group"]?.GetValue<string>() ?? x["row"]?.ToString()}").Distinct());
            foreach (var key in new[] { "verifyNumbered", "verifyUnnumbered" })
            {
                if (n[key]!["pass"]!.GetValue<bool>() != true)
                {
                    refuting.Add($"{key}: {n[key]!["firstFailure"]}");
                }
            }

            var surplus = 0;
            for (var k = 1; k <= i.Parts.Count; k++)
            {
                var t = sliceTi.GetValueOrDefault(Instances.SliceId(i.Instance, k));
                foreach (var f in t?.Failures ?? [])
                {
                    (f.Rule is "TI-2" or "TI-3" or "TI-5" or "TI-6" ? refuting : partial).Add($"part {k} {f.Rule} {f.Class}");
                }

                surplus += t is null ? 0 : t.FailureCount - (t.Failures?.Count ?? 0);
                var e = accounting.GetValueOrDefault(i.Parts[k - 1].Run);
                if (e?.Rules.FirstOrDefault(r => r.Rule == 6) is { Outcome: "FAIL" })
                {
                    partial.Add($"part {k} geometry");
                }

                if (stage.Runs.GetValueOrDefault(i.Parts[k - 1].Run)?.LayoutRepeated == true)
                {
                    partial.Add($"part {k} layout repeated");
                }
            }

            if (surplus > 0)
            {
                (refuting.Count == 0 ? partial : refuting).Add($"unlisted text failures: {surplus} (cause unproven)");
            }

            outcome = refuting.Count > 0 ? (refuting.All(r => r.StartsWith("unlisted", StringComparison.Ordinal)) ? "refuted only by an unclassified surplus (cause unproven)" : "refuted") : partial.Count > 0 ? "partial" : "confirmed";
            foreach (var c in refuting.Concat(partial))
            {
                causes.Add(c);
            }
        }

        node["outcome"] = i.Variant == ReportSpec.Independent ? $"baseline (independent parts): {outcome}" : outcome;
        node["causes"] = causes;
        return node;
    }

    private static JsonObject Statements(JsonArray outcomes, Dictionary<string, JsonNode> observations)
    {
        var result = new JsonObject();
        foreach (var group in outcomes.Where(x => x!["variant"]!.GetValue<string>() == ReportSpec.Handover && x["report"]!.GetValue<string>() != "XB-PROBE").GroupBy(x => x!["case"]!.GetValue<string>()).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            string Rule(string prefix) => list.All(x => !x!["causes"]!.AsArray().Any(c => c!.GetValue<string>().StartsWith(prefix, StringComparison.Ordinal)))
                ? "confirmed" : string.Join("; ", list.SelectMany(x => x!["causes"]!.AsArray().Select(c => c!.GetValue<string>()).Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).Select(c => $"{x["instance"]}: {c}")));
            var obs = list.Select(x => observations.GetValueOrDefault(x!["instance"]!.GetValue<string>())).Where(x => x is not null).ToList();
            int Count(string key, bool produced) => obs.Sum(x => x![key]!.AsArray().Count(b => b!["produced"]!.GetValue<bool>() == produced));
            result[group.Key] = new JsonObject
            {
                ["instances"] = new JsonArray([.. list.Select(x => (JsonNode)x!["instance"]!.GetValue<string>())]),
                ["outcomes"] = new JsonArray([.. list.Select(x => (JsonNode)x!["outcome"]!.GetValue<string>())]),
                ["g1Headers"] = Rule("RG-1"),
                ["g2GroupTotals"] = Rule("RG-2"),
                ["g3MembershipAndPartTotals"] = list.All(x => !x!["causes"]!.AsArray().Any(c => c!.GetValue<string>().StartsWith("RG-3", StringComparison.Ordinal) || c.GetValue<string>().StartsWith("RG-4", StringComparison.Ordinal))) ? "confirmed" : Rule("RG-3") + " " + Rule("RG-4"),
                ["g4AccountingAcrossTheBoundary"] = Rule("XA-"),
                ["g4BoundaryChain"] = new JsonObject
                {
                    ["continuous"] = obs.Sum(x => x!["pb7Chain"]!.AsArray().Count(b => b!["state"]!.GetValue<string>() == "continuous")),
                    ["broken"] = obs.Sum(x => x!["pb7Chain"]!.AsArray().Count(b => b!["state"]!.GetValue<string>() == "broken")),
                },
                ["g5GlobalNumbering"] = Rule("verify"),
                ["g6Observations"] = new JsonObject
                {
                    ["behaviour1HeaderNotAloneAtBoundary"] = new JsonObject { ["produced"] = Count("behaviour1AtBoundary", true), ["notProduced"] = Count("behaviour1AtBoundary", false) },
                    ["behaviour2ContinuationHeaderAtBoundary"] = new JsonObject { ["produced"] = Count("behaviour2AtBoundary", true), ["notProduced"] = Count("behaviour2AtBoundary", false) },
                    ["behaviour2OnInPartContinuationPages"] = new JsonObject
                    {
                        ["produced"] = obs.Sum(x => x!["behaviour2OnInPartContinuationPages"]!.GetValue<int>()),
                        ["notProduced"] = obs.Sum(x => x!["pb2PagesWithoutHeaderInParts"]!.GetValue<int>()),
                    },
                    ["behaviour3TotalWithLastRowAtBoundary"] = new JsonObject { ["produced"] = Count("behaviour3AtBoundary", true), ["notProduced"] = Count("behaviour3AtBoundary", false) },
                    ["headersAlone"] = obs.Sum(x => x!["pb1HeadersAloneCount"]!.GetValue<int>()),
                    ["headersAloneOnAPartLastPage"] = obs.Sum(x => x!["pb1AtPartLastPage"]!.GetValue<int>()),
                    ["totalsSeparated"] = obs.Sum(x => x!["pb3TotalsSeparated"]!.GetValue<int>()),
                },
            };
        }

        var baseline = new JsonObject();
        foreach (var x in outcomes.Where(x => x!["variant"]!.GetValue<string>() == ReportSpec.Independent))
        {
            var obs = observations.GetValueOrDefault(x!["instance"]!.GetValue<string>());
            baseline[x["instance"]!.GetValue<string>()] = new JsonObject
            {
                ["outcome"] = x["outcome"]!.GetValue<string>(),
                ["failureClasses"] = new JsonArray([.. x["causes"]!.AsArray().Select(c => c!.GetValue<string>()).Select(c => c.Split('|').Length > 1 ? $"{c.Split('|')[0]}|{c.Split('|')[1]}" : c).Distinct().Select(c => (JsonNode)c)]),
                ["boundaryChain"] = obs?["pb7Chain"]?.DeepClone(),
            };
        }

        result["baseline"] = baseline;
        return result;
    }

    private static JsonObject Reproducibility(List<ReportInstance> instances, Dictionary<string, JsonNode> report, Dictionary<string, StrictEntry> accounting, Dictionary<string, IntegrityEntry> ti)
    {
        var reps = instances.Where(i => i.Report == "XB-W250-H1").OrderBy(i => i.Repetition).ToList();
        var differences = new JsonArray();
        if (reps.Count != 3 || reps.Any(r => !r.Complete))
        {
            differences.Add($"{reps.Count} repetitions, {reps.Count(r => r.Complete)} complete");
        }
        else
        {
            for (var k = 0; k < reps[0].Parts.Count; k++)
            {
                var a = reps[0].Parts[k];
                foreach (var b in reps.Skip(1).Select(r => r.Parts[k]))
                {
                    if (a.Pages != b.Pages || accounting.GetValueOrDefault(a.Run)?.RowSetsSha256 != accounting.GetValueOrDefault(b.Run)?.RowSetsSha256
                        || ti.GetValueOrDefault(a.Run)?.ContentSha256 != ti.GetValueOrDefault(b.Run)?.ContentSha256)
                    {
                        differences.Add($"part {k + 1}: {a.Run} against {b.Run}");
                    }
                }
            }

            string Signature(ReportInstance r)
            {
                var n = report.GetValueOrDefault(r.Instance);
                return $"{n?["accountingFailures"]?.ToJsonString()}|{n?["groupFailures"]?.AsArray().Select(f => $"{f!["rule"]}{f["class"]}{f["group"]}").Aggregate(string.Empty, (x, y) => x + y)}|{n?["numberedWordsSha256"]}";
            }

            foreach (var r in reps.Skip(1))
            {
                if (Signature(r) != Signature(reps[0]))
                {
                    differences.Add($"{r.Instance}: report results or numbered words differ from {reps[0].Instance}");
                }
            }
        }

        return new JsonObject { ["report"] = "XB-W250-H1", ["repetitions"] = reps.Count, ["agree"] = differences.Count == 0, ["differences"] = differences };
    }

    private static JsonObject Costs(StageFolder stage, List<ReportInstance> instances, Dictionary<string, JsonNode> report)
    {
        var runs = new JsonArray();
        foreach (var p in stage.Plan.Runs)
        {
            var r = stage.Runs.GetValueOrDefault(p.RunId);
            runs.Add(new JsonObject
            {
                ["run"] = p.RunId,
                ["class"] = r?.Outcome,
                ["pages"] = r?.Pages,
                ["layoutMs"] = r?.LayoutMs,
                ["totalMs"] = r?.TotalMs,
                ["wallMs"] = r?.WallMs,
                ["chromiumTreePeakRssKiB"] = r?.ChromiumTreePeakRssKiB,
                ["containerRunningPeakBytes"] = r?.CgroupMemoryPeakBytes,
            });
        }

        var reports = new JsonArray();
        foreach (var i in instances.Where(i => i.Complete))
        {
            var times = i.Parts.Select(p => stage.Runs.GetValueOrDefault(p.Run)?.TotalMs ?? 0).ToList();
            reports.Add(new JsonObject
            {
                ["instance"] = i.Instance,
                ["parts"] = i.Parts.Count,
                ["pages"] = i.TotalPages,
                ["partTimeSumMs"] = Math.Round(times.Sum(), 1),
                ["partTimeMaxMs"] = Math.Round(times.Max(), 1),
                ["chromiumTreePeakMaxKiB"] = i.Parts.Select(p => stage.Runs.GetValueOrDefault(p.Run)?.ChromiumTreePeakRssKiB ?? 0).Max(),
                ["containerRunningPeakMaxBytes"] = i.Parts.Select(p => stage.Runs.GetValueOrDefault(p.Run)?.CgroupMemoryPeakBytes ?? 0).Max(),
                ["assembly"] = report.GetValueOrDefault(i.Instance)?["assembly"]?.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["note"] = "per run: the run's total time and both memory figures (the Chromium process-tree peak, and the container running peak); per report: the sum and the maximum of its parts' total times, and the assembly's wall time (informational)",
            ["runs"] = runs,
            ["reports"] = reports,
        };
    }
}
