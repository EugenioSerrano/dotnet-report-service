using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TextIntegrityCheck;

/// <summary>
/// The verdict: exit 0 only when the instrument is healthy — the oracle is
/// trusted, every planned run has a record and an integrity entry, the ordinary
/// 1 000-record block passes in both markups and repeats identically, escaping
/// holds and no run ended in a harness error. Integrity results of tall rows and
/// long free text are results, never part of the exit code.
/// </summary>
internal static class VerdictCommand
{
    public static async Task<int> RunAsync(string runsDirectory, string integrityPath, string controlsPath, string matrix, string? summaryPath)
    {
        var reasons = new List<string>();
        var notes = new List<string>();
        var plan = RunFiles.ReadPlan(runsDirectory);
        var records = RunFiles.ReadRuns(runsDirectory);
        var integrityNode = JsonNode.Parse(await File.ReadAllTextAsync(integrityPath))!.AsObject();
        var integrity = integrityNode["runs"]!.Deserialize<List<IntegrityRecord>>(RunFiles.Json)!;
        var controlsNode = JsonNode.Parse(await File.ReadAllTextAsync(controlsPath))!.AsObject();
        var controls = controlsNode.Deserialize<ControlsFile>(RunFiles.Json)!;
        var byRun = integrity.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        var recordById = records.ToDictionary(r => r.RunId, StringComparer.Ordinal);
        var full = matrix == "full";

        if (plan.Matrix != matrix)
        {
            reasons.Add($"plan.json is for matrix '{plan.Matrix}', not '{matrix}'");
        }

        // Every planned run has a record (any class, not-run included) and an integrity entry.
        foreach (var planned in plan.Runs)
        {
            if (!recordById.ContainsKey(planned.RunId))
            {
                reasons.Add($"planned run {planned.RunId} has no record in runs.jsonl");
            }
            else if (!byRun.ContainsKey(planned.RunId))
            {
                reasons.Add($"run {planned.RunId} has no integrity entry");
            }
        }

        IntegrityRecord? Find(string fixture, string markup, int repetition = 1, string? mutation = null) =>
            integrity.FirstOrDefault(r => r.FixtureId == fixture && r.Markup == markup && r.Repetition == repetition && r.Mutation == mutation);

        // Oracle trust — positive controls.
        var positives = full ? new[] { ("B100", "table"), ("B100", "grid"), ("K1", "table"), ("K1", "grid") } : [("B100", "table"), ("B100", "grid")];
        foreach (var (fixture, markup) in positives)
        {
            var r = Find(fixture, markup);
            if (r?.Verdict != "pass")
            {
                reasons.Add($"oracle untrusted: positive control {fixture} {markup} is {r?.Verdict ?? "missing"}" + (r?.FailureClasses is { Count: > 0 } c ? $" [{string.Join(' ', c)}]" : string.Empty));
            }
        }

        // Oracle trust — HTML-level negative controls.
        var required = full
            ? new[] { ("NC-W", "B100"), ("NC-L", "B100"), ("NC-R", "B100"), ("NC-F", "F26K-a") }
            : [("NC-W", "B100")];
        foreach (var (mutation, fixture) in required)
        {
            var r = Find(fixture, "table", 1, mutation);
            if (r?.Control?.Behaved != true)
            {
                reasons.Add($"oracle untrusted: control {mutation} on {fixture} " + (r is null ? "missing" : $"did not fail as expected ({r.Verdict}, classes [{string.Join(' ', r.FailureClasses ?? [])}])"));
            }
        }

        var t300u = Find("T300u", "table");
        foreach (var r in integrity.Where(r => r.Mutation is not null && r.FixtureId == "T300u"))
        {
            if (t300u?.Verdict != "pass")
            {
                notes.Add($"control {r.Mutation} on T300u: not evaluable (unmutated T300u table is {t300u?.Verdict ?? "missing"})");
            }
            else if (r.Control?.Behaved != true)
            {
                reasons.Add($"oracle untrusted: control {r.Mutation} on T300u did not fail as expected ([{string.Join(' ', r.FailureClasses ?? [])}])");
            }
        }

        // Oracle trust — extraction-level controls.
        foreach (var c in controls.Controls)
        {
            var mandatory = c.Id is "E1" or "E2" or "E3" or "E4";
            if (c.Behaved == "yes")
            {
                continue;
            }

            if (c.Behaved == "not evaluable" && !mandatory)
            {
                notes.Add($"{c.Id}: not evaluable ({c.Reason})");
                continue;
            }

            reasons.Add($"oracle untrusted: extraction-level control {c.Id} {c.Behaved} — {c.Mutation}; expected {c.Expected}" + (c.Reason is null ? string.Empty : $" ({c.Reason})"));
        }

        var trusted = !reasons.Any(r => r.StartsWith("oracle untrusted", StringComparison.Ordinal));

        // The ordinary 1 000-record block: passes in both markups, every repetition, identical pages and text.
        if (full)
        {
            foreach (var markup in new[] { "table", "grid" })
            {
                var reps = Enumerable.Range(1, 3).Select(i => Find("B1000", markup, i)).ToList();
                foreach (var (r, i) in reps.Select((r, i) => (r, i + 1)))
                {
                    if (r?.Verdict != "pass")
                    {
                        reasons.Add($"B1000 {markup} repetition {i} is {r?.Verdict ?? "missing"}" + (r?.FailureClasses is { Count: > 0 } c ? $" [{string.Join(' ', c)}]" : string.Empty));
                    }
                }

                var passing = reps.Where(r => r?.Verdict == "pass").ToList();
                if (passing.Select(r => r!.Pages).Distinct().Count() > 1)
                {
                    reasons.Add($"B1000 {markup}: page counts differ across repetitions ({string.Join(", ", passing.Select(r => r!.Pages))})");
                }

                if (passing.Select(r => r!.ContentSha256).Distinct().Count() > 1)
                {
                    reasons.Add($"B1000 {markup}: extracted text differs across repetitions");
                }
            }
        }

        // Escaping and harness errors.
        foreach (var r in records)
        {
            if (r.Outcome == "ok" && (r.Dialogs != 0 || r.AbortedRequests != 0 || r.Scripts != 0))
            {
                reasons.Add($"{r.RunId}: escaping check failed (dialogs {r.Dialogs}, aborted requests {r.AbortedRequests}, scripts {r.Scripts})");
            }

            if (r.Outcome == "error")
            {
                reasons.Add($"{r.RunId}: harness error — {r.Reason}");
            }
        }

        var exitCode = reasons.Count == 0 ? 0 : 1;
        Console.WriteLine($"Oracle {(trusted ? "trusted" : "UNTRUSTED")}; {records.Count} run records for {plan.Runs.Count} planned runs.");
        foreach (var note in notes)
        {
            Console.WriteLine("note: " + note);
        }

        foreach (var reason in reasons)
        {
            Console.WriteLine("reason: " + reason);
        }

        Console.WriteLine($"Verdict exit code {exitCode}.");

        if (summaryPath is not null)
        {
            var harnessSummary = Path.Combine(runsDirectory, "summary.json");
            var summary = new JsonObject
            {
                ["harness"] = File.Exists(harnessSummary) ? JsonNode.Parse(await File.ReadAllTextAsync(harnessSummary)) : null,
                ["checker"] = integrityNode["checker"]?.DeepClone(),
                ["integrity"] = JsonSerializer.SerializeToNode(integrity.Select(r => new
                {
                    r.RunId,
                    r.FixtureId,
                    r.Markup,
                    r.Repetition,
                    r.Mutation,
                    r.Outcome,
                    r.Verdict,
                    r.Reason,
                    r.Pages,
                    r.ContentSha256,
                    r.FailureCount,
                    r.FailureClasses,
                    firstFailures = r.Failures?.Take(3),
                    r.Rules,
                    tallRows = r.Observations?.TallRows,
                    rowsSpanningThreeOrMorePages = r.Observations?.RowsSpanningThreeOrMorePages,
                    headerRowsPerPageMax = r.Observations?.HeaderRowsPerPage.Values.DefaultIfEmpty(0).Max(),
                    wordBoundaryDifferences = r.Observations?.TextsWithWordBoundaryDifferences,
                    rawLigatures = r.Observations?.RawLigatures,
                    groupStartsLastLineOfPage = r.Observations?.GroupStarts.Count(g => g.LastLineOfPage),
                    r.Control,
                }), RunFiles.Json),
                ["controls"] = controlsNode.DeepClone(),
                ["verdict"] = new JsonObject
                {
                    ["matrix"] = matrix,
                    ["oracleTrusted"] = trusted,
                    ["exitCode"] = exitCode,
                    ["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)JsonValue.Create(r))]),
                    ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)JsonValue.Create(n))]),
                    ["decidedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                },
            };
            await File.WriteAllTextAsync(summaryPath, summary.ToJsonString(RunFiles.Json) + "\n");
            Console.WriteLine($"Summary written to {summaryPath}.");
        }

        return exitCode;
    }
}
