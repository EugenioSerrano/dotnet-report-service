using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAccountingCheck;
using PaginationFixtures;
using StrictAccountingCheck;

namespace GroupContinuityCheck;

/// <summary>The group check's entry for one planned run (groups.json).</summary>
internal sealed class GroupEntry
{
    public string Run { get; set; } = string.Empty;

    public string FixtureId { get; set; } = string.Empty;

    public bool FooterOn { get; set; }

    /// <summary>checked | no-pdf | no-check.</summary>
    public string Verdict { get; set; } = "no-pdf";

    public string? Reason { get; set; }

    public GroupResult? Result { get; set; }

    public double CheckSeconds { get; set; }
}

/// <summary>The "groups" command: the group check and the placement observations of every planned run.</summary>
internal static class GroupsCommand
{
    public static async Task<int> RunAsync(string fixturesDirectory, string runsDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var stage = StageFolder.Load(runsDirectory);
        var entries = new List<GroupEntry>();
        foreach (var planned in stage.Plan.Runs)
        {
            var entry = new GroupEntry { Run = planned.RunId, FixtureId = planned.FixtureId, FooterOn = planned.FooterOn };
            entries.Add(entry);
            var run = stage.Runs.GetValueOrDefault(planned.RunId);
            if (run is not { Outcome: "ok", Pdf: not null } || !File.Exists(Path.Combine(stage.Directory, run.Pdf)))
            {
                entry.Reason = run is null ? "no run record" : run.Outcome;
                continue;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                var fixture = FixtureFile.ReadVerified(fixturesDirectory, planned.FixtureId, sums);
                var reading = await Account.ReadAsync(Path.Combine(stage.Directory, run.Pdf), fixture, planned.FooterOn, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
                entry.Result = GroupCheck.Check(reading, fixture);
                entry.Verdict = "checked";
            }
            catch (Exception e) when (e is TimeoutException or InvalidDataException or IOException or System.Xml.XmlException or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException or OutOfMemoryException)
            {
                entry.Verdict = "no-check";
                entry.Reason = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
            }

            entry.CheckSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            var r = entry.Result;
            Console.WriteLine($"{entry.Run}: {entry.Verdict}" + (r is null ? $" ({entry.Reason})" :
                $", failures {r.Failures.Count} [{string.Join(" ", r.Failures.Select(f => f.Key).Distinct().Take(6))}], headers alone {r.HeadersAlone}, repeated {r.HeadersRepeated}, "
                + $"pages without header {r.ContinuationPagesWithoutHeader}, totals separated {r.TotalsSeparated}, fixture total defects {r.FixtureTotalDefects.Count}"));
        }

        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "groups.json"), new { runs = entries });
        return 0;
    }

    public static List<GroupEntry> Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!["runs"]!.Deserialize<List<GroupEntry>>(StageFolder.Json) ?? [];
}

/// <summary>
/// The "probe" command: the strict check and the group check on the four committed
/// PDFs of spikes/08-pagedjs-pre-split, with each one's source fixture. Its known
/// answers are only what that experiment established: on the three PDFs with the
/// split every row printed (rule 1) and the chain closes on the block total (rule 3);
/// on the giant-row PDF without the split every row after row 50 is absent. The
/// group check on these PDFs is an observation.
/// </summary>
internal static class ProbeCommand
{
    private static readonly (string File, string Fixture, bool Split)[] Pdfs =
    [
        ("G200-split-t1.00-f0.90-on-r1.pdf", "G200", true),
        ("X1M-split-t1.00-f0.90-on-r1.pdf", "X1M", true),
        ("W2a-split-t1.00-f0.90-on-r1.pdf", "W2a", true),
        ("G200-off-on-r1.pdf", "G200", false),
    ];

    public static async Task<int> RunAsync(string evidenceDirectory, string fixturesDirectory, string referenceDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var results = new List<object>();
        var all = true;
        foreach (var (file, fixtureId, split) in Pdfs)
        {
            // W2a is not in this experiment's fixture folder: it is read from the shared folder by its own list.
            var directory = File.Exists(FixtureFile.PathOf(fixturesDirectory, fixtureId)) ? fixturesDirectory : referenceDirectory;
            var fixture = FixtureFile.ReadVerified(directory, fixtureId, Sums.Read(Path.Combine(directory, FixtureFile.SumsFileName)));
            var reading = await Account.ReadAsync(Path.Combine(evidenceDirectory, file), fixture, true, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            var entry = new StrictEntry { Run = file, Markup = "grid", FixtureId = fixtureId, FooterOn = true, Repetition = 1 };
            Account.Judge(entry, reading, fixture, null, null);
            var groups = GroupCheck.Check(reading, fixture);
            var rule1 = entry.Rules.First(r => r.Rule == 1);
            var rule3 = entry.Rules.First(r => r.Rule == 3);
            string known;
            string observed;
            bool holds;
            if (split)
            {
                known = "rule 1 PASS; rule 3 PASS (every row printed; the chain closes on the block total)";
                observed = $"rule 1 {rule1.Outcome} ({rule1.Detail}); rule 3 {rule3.Outcome} ({rule3.Detail})";
                holds = rule1.Outcome == "PASS" && rule3.Outcome == "PASS";
            }
            else
            {
                var after = fixture.Items.Select(i => i.Sequence!.Value).Where(s => s > 50).ToList();
                var present = after.Where(reading.RowPages.ContainsKey).ToList();
                known = $"rule 1 FAIL; none of the {after.Count} sequences after 50 in the reading";
                observed = $"rule 1 {rule1.Outcome} ({rule1.Detail}); {present.Count} of them present";
                holds = rule1.Outcome == "FAIL" && present.Count == 0;
            }

            all &= holds;
            Console.WriteLine($"{file}: {(holds ? "holds" : "DOES NOT HOLD")} — {observed}; group check (observation): {string.Join(" ", groups.Failures.Select(f => f.Key).Distinct())}");
            results.Add(new
            {
                pdf = file,
                fixture = fixtureId,
                knownAnswer = known,
                observedKnown = observed,
                holds,
                observations = new { rules = entry.Rules, groupFailures = groups.Failures, groups = groups.Groups },
            });
        }

        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "probe.json"), new { knownAnswersHold = all, pdfs = results });
        return all ? 0 : 1;
    }
}

/// <summary>
/// The "anchors" command: every run of this experiment that the grid experiment of
/// spikes/08-pagedjs-pre-split also ran, beside that run (its committed summary.json
/// and integrity.json), field by field. Every difference is listed; times and memory
/// are set beside, never compared.
/// </summary>
internal static class AnchorsCommand
{
    public const string Prefix = "s1-grid-";

    public static bool IsAnchor(string run) => run.StartsWith(Prefix, StringComparison.Ordinal) && !run.StartsWith(Prefix + "new-", StringComparison.Ordinal);

    public static async Task<int> RunAsync(string referenceSummaryPath, string referenceIntegrityPath, string accountingPath, string runsDirectory, string outPath)
    {
        var reference = JsonNode.Parse(File.ReadAllText(referenceSummaryPath))!;
        var referenceRuns = reference["runs"]!.AsArray().ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var referenceTi = JsonNode.Parse(File.ReadAllText(referenceIntegrityPath))!["runs"]!.AsArray().ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var accounting = GroupControls.ReadAccounting(accountingPath);
        var stage = StageFolder.Load(runsDirectory);
        var rows = new JsonArray();
        var differences = 0;
        var compared = 0;
        foreach (var e in accounting.Where(e => IsAnchor(e.Run)))
        {
            var id = e.Run[Prefix.Length..];
            var run = stage.Runs.GetValueOrDefault(e.Run);
            var row = new JsonObject { ["run"] = e.Run, ["reference"] = id };
            var listed = new JsonArray();
            if (!referenceRuns.TryGetValue(id, out var r))
            {
                listed.Add("no run of that id in the reference");
            }
            else
            {
                void Field(string name, object? mine, object? theirs)
                {
                    var a = Convert.ToString(mine, CultureInfo.InvariantCulture);
                    var b = Convert.ToString(theirs, CultureInfo.InvariantCulture);
                    row[name] = new JsonObject { ["here"] = a, ["reference"] = b };
                    if (a != b)
                    {
                        listed.Add($"{name}: {a} here, {b} in the reference");
                    }
                }

                Field("class", e.RunClass, r["outcome"]?.GetValue<string>());
                Field("libraryPages", run?.Pages, r["pages"]?.GetValue<int?>());
                Field("parts", run?.Parts, r["splitResult"]?["partsTotal"]?.GetValue<int?>());
                Field("libraryRules", run?.OwnRules, r["rules"] is JsonArray rules
                    ? string.Join(' ', rules.Select(x => $"{x!["name"]!.GetValue<string>()}:{(x["pass"]!.GetValue<bool>() ? "P" : "F")}"))
                    : null);
                Field("layoutRepeated", e.LayoutRepeated, r["layoutRepeatedError"]?.GetValue<bool>() == true || r["layoutRepeatedAnyLevel"]?.GetValue<bool>() == true);
                Field("semInnerSha256", e.SemInnerSha256, r["sem"]?["pdfInnerSha256"]?.GetValue<string>());
                Field("htmlBytes", e.HtmlBytes, r["htmlBytes"]?.GetValue<long?>());
                var ti = referenceTi.GetValueOrDefault(id);
                Field("textVerdict", e.TextVerdict, ti?["verdict"]?.GetValue<string>());
                Field("contentSha256", e.ContentSha256, ti?["contentSha256"]?.GetValue<string>());
                Field("pdfPages", e.Pages, ti?["pages"]?.GetValue<int?>());
            }

            compared++;
            differences += listed.Count;
            row["differences"] = listed;
            rows.Add(row);
        }

        var document = new JsonObject { ["runsCompared"] = compared, ["differences"] = differences, ["runs"] = rows };
        await File.WriteAllTextAsync(outPath, document.ToJsonString(StageFolder.Json) + "\n");
        Console.WriteLine($"{compared} shared runs compared with the grid experiment: {differences} differences.");
        foreach (var row in rows.Where(x => x!["differences"]!.AsArray().Count > 0))
        {
            Console.WriteLine($"  {row!["run"]}: {string.Join("; ", row["differences"]!.AsArray().Select(d => d!.GetValue<string>()))}");
        }

        return 0;
    }
}
