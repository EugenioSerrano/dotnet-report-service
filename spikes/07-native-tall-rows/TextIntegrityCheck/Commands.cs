using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaginationFixtures;

namespace TextIntegrityCheck;

/// <summary>Whether a negative-control run failed for the expected reason at the expected place.</summary>
internal sealed record ControlCheck(string ExpectedClass, int? ExpectedRow, string? ExpectedRegion, bool Behaved);

/// <summary>The checker's record of one run in integrity.json.</summary>
internal sealed class IntegrityRecord
{
    public required string RunId { get; init; }

    public required string FixtureId { get; init; }

    public required string Markup { get; init; }

    public int Repetition { get; init; }

    public string? Mutation { get; init; }

    public required string Outcome { get; init; }

    /// <summary>pass | fail | no-pdf | no-check.</summary>
    public required string Verdict { get; set; }

    public string? Reason { get; set; }

    public int? Pages { get; set; }

    public string? ContentSha256 { get; set; }

    public List<int>? PageWordCounts { get; set; }

    public int FailureCount { get; set; }

    public List<string>? FailureClasses { get; set; }

    public List<Failure>? Failures { get; set; }

    public SortedDictionary<string, string>? Rules { get; set; }

    public Observations? Observations { get; set; }

    public ControlCheck? Control { get; set; }

    public double CheckSeconds { get; set; }
}

internal sealed class IntegrityFile
{
    public required JsonObject Checker { get; init; }

    public required List<IntegrityRecord> Runs { get; init; }
}

internal sealed class ControlsFile
{
    public string? BaseRun { get; init; }

    public string? BaseFixture { get; init; }

    public required string BaseChoice { get; init; }

    public required List<ControlOutcome> Controls { get; init; }
}

internal static class Commands
{
    private const int MaxFailuresKept = 100;

    public static int Fixtures(string datasetPath, string sumsPath, string outDirectory, IReadOnlyList<string>? only)
    {
        Dataset dataset;
        try
        {
            dataset = Dataset.Load(datasetPath, sumsPath);
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        Console.WriteLine($"Dataset {dataset.FileName} sha256 {dataset.Sha256} ({dataset.Records.Count} records) verified.");
        Directory.CreateDirectory(outDirectory);
        var series = new FixtureSeries(dataset);
        var ids = only ?? FixtureSeries.AllIds;
        foreach (var id in ids)
        {
            Fixture fixture;
            try
            {
                fixture = series.Build(id);
            }
            catch (InvalidDataException e)
            {
                Console.Error.WriteLine($"Assertion failed: {e.Message}");
                return 1;
            }

            FixtureFile.Write(outDirectory, fixture);
            var tall = fixture.Items.FirstOrDefault(r => r.Sequence == FixtureSeries.TallRowSequence);
            var record = tall is null ? null : dataset.Records.First(r => r.Sequence == tall.Sequence);
            var recordBytes = record is null ? 0 : FixtureFile.CompactItemBytes(record, tall!.Description!);
            Console.WriteLine(
                $"{id}: {fixture.Items.Count()} items, row 50 description {tall?.Description?.Length ?? 0} chars, record {recordBytes} bytes, "
                + $"free text before {fixture.FreeTextBefore.Sum(FixtureFile.Utf8Bytes)} bytes in {fixture.FreeTextBefore.Count} paragraphs, "
                + $"after {fixture.FreeTextAfter.Sum(FixtureFile.Utf8Bytes)} bytes in {fixture.FreeTextAfter.Count}"
                + (fixture.Id == "G200" ? $", row 120 {fixture.Items.First(r => r.Sequence == 120).Description!.Length} chars" : string.Empty));
        }

        Sums.WriteFolder(outDirectory, Path.Combine(outDirectory, FixtureFile.SumsFileName));
        Console.WriteLine($"{ids.Count} fixtures and {FixtureFile.SumsFileName} written to {outDirectory}.");
        return 0;
    }

    public static async Task<int> CheckAsync(string fixturesDirectory, string runsDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var startedAt = DateTimeOffset.Now;
        var poppler = await PopplerVersionAsync(cancellation);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var fixtures = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        var records = RunFiles.ReadRuns(runsDirectory);
        var results = new List<IntegrityRecord>();

        // Journal: one line when a run's check starts, one with its record when it ends. A re-run
        // reuses finished records, and a run whose check started but never ended (the checker was
        // killed, for example by its memory cap) is recorded as no-check with that reason.
        var journalPath = Path.Combine(outDirectory, "integrity.journal.jsonl");
        var finished = new Dictionary<string, IntegrityRecord>(StringComparer.Ordinal);
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
                    var previous = node.Deserialize<IntegrityRecord>(RunFiles.Json)!;
                    finished[previous.RunId] = previous;
                }
            }
        }

        await using var journal = new StreamWriter(journalPath, append: true) { AutoFlush = true, NewLine = "\n" };
        foreach (var run in records)
        {
            if (finished.TryGetValue(run.RunId, out var done) && done.Outcome == run.Outcome)
            {
                results.Add(done);
                continue;
            }

            if (started.Contains(run.RunId) && !finished.ContainsKey(run.RunId))
            {
                var died = new IntegrityRecord
                {
                    RunId = run.RunId,
                    FixtureId = run.FixtureId,
                    Markup = run.Markup,
                    Repetition = run.Repetition,
                    Mutation = run.Mutation,
                    Outcome = run.Outcome,
                    Verdict = "no-check",
                    Reason = "the checker process ended while checking this run (killed, for example by its memory cap)",
                };
                results.Add(died);
                await journal.WriteLineAsync(JsonSerializer.Serialize(died, RunFiles.JsonCompact));
                Console.WriteLine($"{run.RunId}: no-check — {died.Reason}");
                continue;
            }

            await journal.WriteLineAsync(JsonSerializer.Serialize(new { started = run.RunId }, RunFiles.JsonCompact));
            var result = new IntegrityRecord
            {
                RunId = run.RunId,
                FixtureId = run.FixtureId,
                Markup = run.Markup,
                Repetition = run.Repetition,
                Mutation = run.Mutation,
                Outcome = run.Outcome,
                Verdict = "no-pdf",
            };
            results.Add(result);
            var pdf = run.Pdf is null ? null : Path.Combine(runsDirectory, run.Pdf);
            if (run.Outcome != "ok" || pdf is null || !File.Exists(pdf))
            {
                result.Reason = run.Outcome == "ok" ? "ok without a PDF file" : run.Outcome + (run.Reason is null ? string.Empty : $" ({run.Reason})");
                Console.WriteLine($"{run.RunId}: no-pdf — {result.Reason}");
                await journal.WriteLineAsync(JsonSerializer.Serialize(result, RunFiles.JsonCompact));
                continue;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                if (!fixtures.TryGetValue(run.FixtureId, out var fixture))
                {
                    fixture = FixtureFile.ReadVerified(fixturesDirectory, run.FixtureId, sums);
                    fixtures[run.FixtureId] = fixture;
                }

                var text = await Poppler.ExtractAsync(pdf, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
                var analysis = new IntegrityRules(fixture, run.Marks, run.Footer).Analyze(PrintedDocument.From(text));
                result.Verdict = analysis.Pass ? "pass" : "fail";
                result.Pages = analysis.Pages;
                result.ContentSha256 = analysis.ContentSha256;
                result.PageWordCounts = analysis.PageWordCounts;
                result.FailureCount = analysis.Failures.Count;
                result.FailureClasses = [.. analysis.Failures.Select(f => f.Class).Distinct().Order(StringComparer.Ordinal)];
                result.Failures = [.. analysis.Failures.Take(MaxFailuresKept)];
                result.Rules = analysis.Rules;
                result.Observations = analysis.Observations;
                if (run.Mutation is not null)
                {
                    var (expectedClass, row, region) = ControlMutations.Expected(run.Mutation);
                    var behaved = analysis.Failures.Any(f => f.Class == expectedClass && (row is null || f.Row == row) && (region is null || f.Region == region));
                    result.Control = new ControlCheck(expectedClass, row, region, behaved);
                }
            }
            catch (Exception e) when (e is TimeoutException or InvalidDataException or IOException or System.Xml.XmlException or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException or OutOfMemoryException)
            {
                result.Verdict = "no-check";
                result.Reason = $"{e.GetType().Name}: {e.Message}";
            }

            result.CheckSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
            var tall = result.Observations?.TallRows.FirstOrDefault();
            Console.WriteLine($"{run.RunId}: {result.Verdict}"
                + (result.FailureClasses is { Count: > 0 } ? $" [{string.Join(' ', result.FailureClasses)}]" : string.Empty)
                + (result.Pages is null ? string.Empty : $", {result.Pages} pages")
                + (tall is null ? string.Empty : $", row {tall.Row} on pages {tall.FirstPage}–{tall.LastPage}")
                + (result.Control is null ? string.Empty : $", control {(result.Control.Behaved ? "behaved" : "DID NOT BEHAVE")}")
                + (result.Reason is null ? string.Empty : $" — {result.Reason}"));
            await journal.WriteLineAsync(JsonSerializer.Serialize(result, RunFiles.JsonCompact));
        }

        var checker = new JsonObject
        {
            ["poppler"] = poppler,
            ["startedAt"] = startedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["endedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeoutSeconds"] = timeoutSeconds,
            ["fixtureSumsSha256"] = Sums.HashFile(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName)),
            ["normalisation"] = "NFKC, then every whitespace character removed; nothing else",
        };
        await WriteJsonAsync(Path.Combine(outDirectory, "integrity.json"), new IntegrityFile { Checker = checker, Runs = results });
        Console.WriteLine($"{results.Count} runs checked: {results.Count(r => r.Verdict == "pass")} pass, {results.Count(r => r.Verdict == "fail")} fail, "
            + $"{results.Count(r => r.Verdict == "no-pdf")} no-pdf, {results.Count(r => r.Verdict == "no-check")} no-check.");
        return 0;
    }

    public static async Task<int> ControlsAsync(string fixturesDirectory, string runsDirectory, string outDirectory, string? baseRun, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var records = RunFiles.ReadRuns(runsDirectory).Where(r => r.Mutation is null && r.Outcome == "ok" && r.Pdf is not null).ToList();
        var known = ReadIntegrity(Path.Combine(outDirectory, "integrity.json"));

        async Task<(PrintedDocument Document, IntegrityResult Result, Fixture Fixture)?> AnalyzeAsync(RunRecord run)
        {
            if (known is not null && known.TryGetValue(run.RunId, out var verdict) && verdict != "pass")
            {
                return null;
            }

            var fixture = FixtureFile.ReadVerified(fixturesDirectory, run.FixtureId, sums);
            var text = await Poppler.ExtractAsync(Path.Combine(runsDirectory, run.Pdf!), TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            var document = PrintedDocument.From(text);
            var result = new IntegrityRules(fixture, run.Marks, run.Footer).Analyze(document);
            return (document, result, fixture);
        }

        (RunRecord Run, PrintedDocument Document, Fixture Fixture, bool MultiPage, string Choice)? chosen = null;
        var candidates = baseRun is not null
            ? records.Where(r => r.RunId == baseRun).Select(r => (r, "named")).ToList()
            : records.Where(r => r.FixtureId == "T300u" && r.Markup == "table").Select(r => (r, "T300u table"))
                .Concat(records.Where(r => !(r.FixtureId == "T300u" && r.Markup == "table")).Select(r => (r, "first passing run with a row over two or more pages")))
                .ToList();
        foreach (var (run, choice) in candidates)
        {
            var analysed = await AnalyzeAsync(run);
            if (analysed is not { } a || !a.Result.Pass)
            {
                continue;
            }

            var multiPage = a.Result.Observations.MultiPageRows.Count > 0;
            if (multiPage || baseRun is not null)
            {
                chosen = (run, a.Document, a.Fixture, multiPage, choice);
                break;
            }
        }

        if (chosen is null && baseRun is null)
        {
            var b1000 = records.FirstOrDefault(r => r.FixtureId == "B1000" && r.Markup == "table");
            if (b1000 is not null && await AnalyzeAsync(b1000) is { } a && a.Result.Pass)
            {
                chosen = (b1000, a.Document, a.Fixture, false, "B1000 table (E1–E4 only)");
            }
        }

        List<ControlOutcome> outcomes;
        if (chosen is { } c)
        {
            outcomes = ExtractionControls.Run(c.Document, c.Fixture, c.MultiPage);
        }
        else
        {
            outcomes = [.. new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E7", "E8" }.Select(id => new ControlOutcome
            {
                Id = id,
                Mutation = "-",
                Expected = "-",
                Behaved = "not evaluable",
                Reason = "no passing base run",
            })];
        }

        foreach (var o in outcomes)
        {
            Console.WriteLine($"{o.Id}: {o.Behaved} — {o.Mutation}; expected {o.Expected}" + (o.Observed.Count > 0 ? $"; observed {string.Join(" | ", o.Observed)}" : string.Empty) + (o.Reason is null ? string.Empty : $" ({o.Reason})"));
        }

        await WriteJsonAsync(Path.Combine(outDirectory, "controls.json"), new ControlsFile
        {
            BaseRun = chosen?.Run.RunId,
            BaseFixture = chosen?.Fixture.Id,
            BaseChoice = chosen?.Choice ?? "none",
            Controls = outcomes,
        });
        Console.WriteLine($"Base run: {chosen?.Run.RunId ?? "none"} ({chosen?.Choice ?? "no passing base"}).");
        return 0;
    }

    public static async Task<int> RenderAsync(string pdf, IReadOnlyList<int> pages, string outDirectory, CancellationToken cancellation)
    {
        var written = await Poppler.RenderAsync(pdf, pages, outDirectory, TimeSpan.FromSeconds(120), cancellation);
        foreach (var file in written)
        {
            Console.WriteLine(file);
        }

        return 0;
    }

    public static async Task<JsonObject> PopplerVersionAsync(CancellationToken cancellation)
    {
        var (_, pdftotext) = await Poppler.RunAsync("pdftotext", ["-v"], TimeSpan.FromSeconds(30), cancellation);
        var (_, pdfinfo) = await Poppler.RunAsync("pdfinfo", ["-v"], TimeSpan.FromSeconds(30), cancellation);
        return new JsonObject
        {
            ["pdftotext"] = pdftotext.Split('\n')[0].Trim(),
            ["pdfinfo"] = pdfinfo.Split('\n')[0].Trim(),
        };
    }

    public static Dictionary<string, string>? ReadIntegrity(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var node = JsonNode.Parse(File.ReadAllText(path));
        return node?["runs"]?.AsArray().ToDictionary(r => r!["runId"]!.GetValue<string>(), r => r!["verdict"]!.GetValue<string>(), StringComparer.Ordinal);
    }

    public static async Task WriteJsonAsync<T>(string path, T value)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, RunFiles.Json);
        await stream.WriteAsync("\n"u8.ToArray());
    }
}
