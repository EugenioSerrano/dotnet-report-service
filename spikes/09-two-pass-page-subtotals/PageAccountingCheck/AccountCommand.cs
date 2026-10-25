using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;
using TextIntegrityCheck;

namespace PageAccountingCheck;

/// <summary>accounting.json: the checker's versions and settings, and one entry per planned run.</summary>
internal sealed class AccountingFile
{
    public required JsonObject Checker { get; init; }

    public required List<AccountingEntry> Runs { get; init; }
}

/// <summary>
/// The "account" command: one entry per planned run, in plan order. A journal
/// (accounting.journal.jsonl) records when a run's check starts and its entry when
/// it ends; a re-run reuses finished entries, and a run whose check started but
/// never ended is recorded as no-check with that reason.
/// </summary>
internal static class AccountCommand
{
    public static async Task<int> RunAsync(
        string fixturesDirectory, string runsDirectory, string integrityPath, string? referenceIntegrityPath, string outDirectory, string workDirectory,
        int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        Directory.CreateDirectory(workDirectory);
        var startedAt = DateTimeOffset.Now;
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var sumsPath = Path.Combine(fixturesDirectory, FixtureFile.SumsFileName);
        var sums = Sums.Read(sumsPath);
        var plan = Files.ReadPlan(runsDirectory);
        var runs = Files.ReadTwoPass(runsDirectory);
        var integrity = Files.ReadIntegrity(integrityPath);
        var reference = referenceIntegrityPath is null ? null : Files.ReadIntegrity(referenceIntegrityPath);
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);

        var journalPath = Path.Combine(outDirectory, "accounting.journal.jsonl");
        var finished = new Dictionary<string, AccountingEntry>(StringComparer.Ordinal);
        var started = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(journalPath))
        {
            foreach (var line in File.ReadLines(journalPath).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var node = JsonNode.Parse(line)!;
                if (node["started"] is not null)
                {
                    started.Add(node["started"]!.GetValue<string>());
                }
                else
                {
                    var previous = node.Deserialize<AccountingEntry>(Files.Json)!;
                    finished[previous.Run] = previous;
                }
            }
        }

        var entries = new List<AccountingEntry>();
        await using var journal = new StreamWriter(journalPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var planned in plan.Runs)
        {
            if (finished.TryGetValue(planned.Run, out var done))
            {
                entries.Add(done);
                continue;
            }

            var entry = new AccountingEntry
            {
                Run = planned.Run,
                FixtureId = planned.FixtureId,
                Markup = planned.Markup,
                Strategy = planned.Strategy,
                Repetition = planned.Repetition,
                Mutation = planned.Mutation,
                Fault = planned.Fault,
                Group = planned.Group,
            };
            entries.Add(entry);
            if (started.Contains(planned.Run))
            {
                entry.Verdict = "no-check";
                entry.Outcome = "inconclusive (no-check)";
                entry.Reason = "the checker process ended while checking this run (killed, for example by its memory cap)";
                await journal.WriteLineAsync(JsonSerializer.Serialize(entry, Files.JsonCompact));
                Console.WriteLine($"{planned.Run}: no-check — {entry.Reason}");
                continue;
            }

            if (!runs.TryGetValue(planned.Run, out var run))
            {
                entry.Verdict = "no-check";
                entry.Outcome = "inconclusive (no run record)";
                entry.Reason = "no twopass.jsonl record for this planned run";
                await journal.WriteLineAsync(JsonSerializer.Serialize(entry, Files.JsonCompact));
                continue;
            }

            entry.Pass1Outcome = run.Pass1.Outcome;
            entry.Pass2Outcome = run.Pass2.Outcome;
            await journal.WriteLineAsync(JsonSerializer.Serialize(new { started = planned.Run }, Files.JsonCompact));
            var watch = Stopwatch.StartNew();
            try
            {
                if (!fixtures.TryGetValue(run.FixtureId, out var fixture))
                {
                    fixture = FixtureFile.ReadVerified(fixturesDirectory, run.FixtureId, sums);
                    fixtures[run.FixtureId] = fixture;
                }

                await Evaluation.EvaluateAsync(entry, run, fixture, runsDirectory, integrity, reference, workDirectory, timeout, cancellation);
            }
            catch (Exception e) when (e is TimeoutException or InvalidDataException or IOException or System.Xml.XmlException or JsonException
                or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException)
            {
                entry.Verdict = "no-check";
                entry.Outcome = "inconclusive (no-check)";
                entry.Reason = $"{e.GetType().Name}: {e.Message}";
            }

            entry.CheckSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{entry.Run}: {entry.Verdict}, {entry.Outcome}") + (entry.FailureSet.Count > 0 ? $" [{string.Join(' ', entry.FailureSet.Distinct().Take(8))}]" : string.Empty)
                + (entry.Reason is null ? string.Empty : $" — {entry.Reason}"));
            await journal.WriteLineAsync(JsonSerializer.Serialize(entry, Files.JsonCompact));
        }

        var checker = new JsonObject
        {
            ["poppler"] = await PopplerVersionAsync(cancellation),
            ["qpdf"] = await Qpdf.VersionAsync(cancellation),
            ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["endedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeoutSeconds"] = timeoutSeconds,
            ["fixtureSumsSha256"] = Sums.HashFile(sumsPath),
            ["integrity"] = Sums.HashFile(integrityPath),
            ["referenceIntegrity"] = referenceIntegrityPath is null ? null : Sums.HashFile(referenceIntegrityPath),
            ["regions"] = "R-F y 279–297 mm every page; R-A x 168–198 mm, y 15–279 mm (stamped only); outward to whole pixels at 72 dpi, widened by one pixel",
        };
        await Files.WriteJsonAsync(Path.Combine(outDirectory, "accounting.json"), new AccountingFile { Checker = checker, Runs = entries });
        Console.WriteLine($"{entries.Count} runs: " + string.Join(", ", entries.GroupBy(e => e.Outcome).Select(g => $"{g.Count()} {g.Key}")) + ".");
        return 0;
    }

    public static async Task<string> PopplerVersionAsync(CancellationToken cancellation)
    {
        var (_, output) = await Poppler.RunAsync("pdftotext", ["-v"], TimeSpan.FromSeconds(30), cancellation);
        return output.Split('\n')[0].Trim();
    }
}
