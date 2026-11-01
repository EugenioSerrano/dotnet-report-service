using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TextIntegrityCheck;

namespace ReaderAgreement;

/// <summary>Whether a configuration's repeated extraction files of one PDF are identical.</summary>
internal sealed record DeterminismRow(string Configuration, string Pdf, bool? Identical, int Files);

/// <summary>Phase D (the reference reading alone) and Phase E (each candidate against it).</summary>
internal static class Assessment
{
    private static string ExtractPath(string workDirectory, string stage, string configuration, string id, int repetition) =>
        Path.Combine(workDirectory, stage, "extract", configuration, $"{ExtractCommand.Safe(id)}.r{repetition}.json");

    private static List<RunLine> Runs(string workDirectory, string stage)
    {
        var path = Path.Combine(workDirectory, stage, "runs.jsonl");
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<RunLine>(l, Settings.Json)!)]
            : [];
    }

    /// <summary>Whether the repetitions' extraction files of a configuration are byte-identical; null when fewer than two exist.</summary>
    private static (bool? Identical, int Files) Determinism(string workDirectory, string stage, string configuration, string id, int repetitions)
    {
        var hashes = Enumerable.Range(1, repetitions)
            .Select(r => ExtractPath(workDirectory, stage, configuration, id, r))
            .Where(File.Exists)
            .Select(Files.Sha256)
            .ToList();
        return (hashes.Count < 2 ? null : hashes.Distinct(StringComparer.Ordinal).Count() == 1, hashes.Count);
    }

    // ------------------------------------------------------------------ Phase D

    public static int OracleVerdict(string repositoryRoot, string workDirectory, string fixturesDirectory, string resultsDirectory)
    {
        var problems = new List<string>();
        var runs = Runs(workDirectory, "oracle");
        var pdfs = new List<JsonObject>();
        var ruleControls = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rule in Judged.TextRules.Concat(Judged.StrictRules.Select(Judged.StrictName)))
        {
            ruleControls[rule] = [];
        }

        var excluded = new List<string>();
        foreach (var entry in PdfSet.All)
        {
            var item = new JsonObject { ["id"] = entry.Id, ["use"] = entry.Use, ["regenerated"] = entry.Regenerated };
            pdfs.Add(item);
            var mine = runs.Where(r => r.Pdf == entry.Id).ToList();
            item["runClasses"] = string.Join(',', mine.Select(r => r.Class));
            var first = ExtractPath(workDirectory, "oracle", Readers.OracleWords, entry.Id, 1);
            if (!File.Exists(first))
            {
                item["status"] = "no reading";
                if (entry.Use == "judged")
                {
                    excluded.Add(entry.Id);
                    (entry.Regenerated ? excluded : problems).Add($"{entry.Id}: no reference reading ({item["runClasses"]})");
                }

                continue;
            }

            var (identical, count) = Determinism(workDirectory, "oracle", Readers.OracleWords, entry.Id, Settings.Repetitions[Readers.Oracle]);
            item["repetitionsIdentical"] = identical;
            item["repetitionFiles"] = count;
            if (identical == false)
            {
                problems.Add($"{entry.Id}: the reference reading differs between repetitions");
            }

            var text = ExtractionFile.Read(first).ToPdfText();
            item["pages"] = text.Pages;
            item["words"] = text.Words.Count;
            if (entry.Use != "judged")
            {
                item["status"] = "agreement only";
                continue;
            }

            var fixture = FixturesCommand.Load(fixturesDirectory, entry.FixtureId!);
            var verdicts = Verdicts.Evaluate(entry, Readers.OracleWords, text, fixture);
            item["verdicts"] = JsonSerializer.SerializeToNode(verdicts, Settings.Json);
            var parity = Parity(repositoryRoot, entry, verdicts);
            item["parity"] = JsonSerializer.SerializeToNode(parity, Settings.Json);
            var parityOk = parity.Values.All(p => p.StartsWith("equal", StringComparison.Ordinal) || p.StartsWith("not recorded", StringComparison.Ordinal));
            item["parityHolds"] = parityOk;
            if (!parityOk)
            {
                if (entry.Regenerated)
                {
                    item["status"] = "regenerated, not identical";
                    excluded.Add(entry.Id);
                    continue;
                }

                problems.Add($"{entry.Id}: the reference reading here differs from the recorded entry ({string.Join("; ", parity.Where(p => !p.Value.StartsWith("equal", StringComparison.Ordinal) && !p.Value.StartsWith("not recorded", StringComparison.Ordinal)).Select(p => $"{p.Key} {p.Value}"))})");
            }

            item["status"] = entry.Regenerated ? "regenerated, identical to the record" : "matches the record";
            foreach (var rule in entry.KnownBad)
            {
                var v = verdicts.Judged.FirstOrDefault(j => j.Rule == rule);
                if (v?.Outcome == "fail")
                {
                    ruleControls[rule].Add(entry.Id);
                }
                else
                {
                    problems.Add($"{entry.Id}: known-bad, expected to fail {rule} under the reference reading, but it is {v?.Outcome ?? "missing"}");
                }
            }
        }

        foreach (var (rule, controls) in ruleControls.Where(kv => kv.Value.Count == 0))
        {
            problems.Add($"rule {rule} has no failing control");
        }

        var trusted = problems.Count == 0;
        Files.WriteJson(Path.Combine(resultsDirectory, "oracle.json"), new
        {
            reader = "poppler-utils (pdftotext -bbox, pdfinfo) in the earlier checker image — test-only reference, never a candidate",
            decidedAt = Clock.Now(),
            trusted,
            problems,
            excluded,
            pdfs,
        });
        Files.WriteJson(Path.Combine(resultsDirectory, "oracle-controls.json"), new { decidedAt = Clock.Now(), ruleControls });
        Console.WriteLine(trusted ? "Reference reading trusted." : $"Reference reading NOT trusted: {problems.Count} problem(s).");
        foreach (var p in problems)
        {
            Console.WriteLine("  " + p);
        }

        return trusted ? 0 : 1;
    }

    /// <summary>The reference reading here against the recorded entry, field by field (only what the record holds).</summary>
    private static SortedDictionary<string, string> Parity(string repositoryRoot, PdfEntry entry, ReadingVerdicts here)
    {
        var parity = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var record = Records.Find(repositoryRoot, entry.RecordFolder!, "integrity.json", entry.RecordRunId!);
        void Field(string name, string? recorded, string actual) =>
            parity[name] = recorded is null ? "not recorded" : recorded == actual ? "equal" : $"recorded {recorded}, here {actual}";
        Field("verdict", record?["verdict"]?.GetValue<string>(), here.TextVerdict);
        Field("pages", record?["pages"]?.ToJsonString(), here.Pages.ToString(CultureInfo.InvariantCulture));
        Field("failureCount", record?["failureCount"]?.ToJsonString(), here.FailureCount.ToString(CultureInfo.InvariantCulture));
        Field("contentSha256", record?["contentSha256"]?.GetValue<string>(), here.ContentSha256);
        Field("failureClasses", record?["failureClasses"] is JsonArray classes ? string.Join(',', classes.Select(c => c!.GetValue<string>())) : null, string.Join(',', here.FailureClasses));
        if (record?["rules"] is JsonObject rules)
        {
            foreach (var (rule, value) in rules)
            {
                Field(rule, value!.GetValue<string>(), here.TextRules.GetValueOrDefault(rule, "missing"));
            }
        }

        if (entry.Accounting)
        {
            var strictFolder = entry.StrictRecordFolder ?? entry.RecordFolder!;
            var strictRun = entry.StrictRecordRunId ?? entry.RecordRunId!;
            var strict = Records.Find(repositoryRoot, strictFolder, "accounting.json", strictRun);
            var lines = strict?["rules"] as JsonArray;
            foreach (var n in Judged.StrictRules)
            {
                var recorded = lines?.OfType<JsonObject>().FirstOrDefault(l => l["rule"]?.GetValue<int>() == n);
                var mine = here.StrictRules.FirstOrDefault(l => l.Rule == n);
                string Show(string? outcome, int? page) => outcome is null ? "missing" : outcome == "FAIL" ? $"FAIL p{page?.ToString(CultureInfo.InvariantCulture) ?? "-"}" : outcome;
                var name = $"{Judged.StrictName(n)}" + (entry.StrictRecordFolder is null ? string.Empty : $" (record of {strictFolder} {strictRun})");
                Field(name, recorded is null ? null : Show(recorded["outcome"]?.GetValue<string>(), recorded["page"]?.GetValue<int?>()), Show(mine?.Outcome, mine?.Page));
            }
        }

        return parity;
    }

    // ------------------------------------------------------------------ Phase E

    public static int Compare(string repositoryRoot, string workDirectory, string fixturesDirectory, string resultsDirectory)
    {
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(resultsDirectory, "oracle.json")))!;
        if (oracle["trusted"]?.GetValue<bool>() != true)
        {
            Console.Error.WriteLine("The reference reading is not trusted; nothing is compared.");
            return 1;
        }

        var excluded = oracle["excluded"]!.AsArray().Select(e => e!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var candidateRuns = Runs(workDirectory, "candidates");
        var agreements = new List<AgreementResult>();
        var verdictRows = new List<object>();
        var detection = new List<object>();
        var determinism = new List<DeterminismRow>();
        var observations = new List<object>();
        var labels = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (var configuration in Readers.CandidateConfigurations)
        {
            int verdictsCompared = 0, verdictsAgree = 0, knownBad = 0, detected = 0, pdfsCompared = 0, pdfsAgree = 0, missing = 0;
            foreach (var entry in PdfSet.All.Where(e => !excluded.Contains(e.Id)))
            {
                var referencePath = ExtractPath(workDirectory, "oracle", Readers.OracleWords, entry.Id, 1);
                var candidatePath = ExtractPath(workDirectory, "candidates", configuration, entry.Id, 1);
                if (!File.Exists(referencePath))
                {
                    continue;
                }

                if (!File.Exists(candidatePath))
                {
                    missing++;
                    continue;
                }

                var (identical, files) = Determinism(workDirectory, "candidates", configuration, entry.Id, Settings.Repetitions[Readers.PdfPig]);
                determinism.Add(new DeterminismRow(configuration, entry.Id, identical, files));
                var reference = ExtractionFile.Read(referencePath).ToPdfText();
                var candidate = ExtractionFile.Read(candidatePath).ToPdfText();
                var fixture = entry.FixtureId is null ? null : FixturesCommand.Load(fixturesDirectory, entry.FixtureId);
                var agreement = Agreement.Compare(entry.Id, configuration, reference, candidate, fixture?.Geometry);
                agreements.Add(agreement);
                pdfsCompared++;
                pdfsAgree += agreement.Agrees ? 1 : 0;
                if (entry.Layout == "assembly")
                {
                    observations.Add(Stamps(entry, configuration, reference, candidate));
                }

                if (entry.Use != "judged" || fixture is null)
                {
                    continue;
                }

                var mine = Verdicts.Evaluate(entry, configuration, candidate, fixture);
                var theirs = Verdicts.Evaluate(entry, Readers.OracleWords, reference, fixture);
                foreach (var t in theirs.Judged)
                {
                    var m = mine.Judged.FirstOrDefault(j => j.Rule == t.Rule);
                    var same = mine.Status == "checked" && m is not null && Verdicts.Same(t, m);
                    verdictsCompared++;
                    verdictsAgree += same ? 1 : 0;
                    verdictRows.Add(new { configuration, pdf = entry.Id, rule = t.Rule, reference = t, candidate = m, same, candidateStatus = mine.Status, mine.Reason });
                }

                foreach (var rule in entry.KnownBad)
                {
                    var m = mine.Judged.FirstOrDefault(j => j.Rule == rule);
                    var hit = mine.Status == "checked" && m?.Outcome == "fail";
                    knownBad++;
                    detected += hit ? 1 : 0;
                    detection.Add(new { configuration, pdf = entry.Id, rule, detected = hit, candidate = m });
                }
            }

            var capped = candidateRuns.Where(r => r.Reader == Readers.PdfPig && r.Class != "ok").Select(r => $"{r.Pdf} r{r.Repetition}: {r.Class}").ToList();
            var licence = LicencePasses(resultsDirectory, "PdfPig");
            var deterministic = determinism.Where(d => d.Configuration == configuration).All(d => d.Identical != false);
            var label = !licence ? "insufficient (licence)"
                : missing > 0 || capped.Count > 0 ? "insufficient"
                : verdictsAgree == verdictsCompared && detected == knownBad && pdfsAgree == pdfsCompared && deterministic ? "sufficient on the measured set"
                : verdictsAgree == verdictsCompared && detected == knownBad ? "verdicts agree, extraction differs"
                : "insufficient";
            labels[configuration] = new { label, licence, pdfsCompared, pdfsAgree, verdictsCompared, verdictsAgree, knownBadChecks = knownBad, detected, missing, notOk = capped, deterministic };
        }

        // PDFsharp: a capability record per PDF; positioned words are never produced.
        var probes = PdfSet.All
            .Select(e => Path.Combine(workDirectory, "candidates", "extract", Readers.PdfSharp, $"{ExtractCommand.Safe(e.Id)}.r1.json"))
            .Where(File.Exists)
            .Select(p => JsonSerializer.Deserialize<CapabilityRecord>(File.ReadAllText(p), Settings.Json)!)
            .ToList();
        var sharpRuns = candidateRuns.Where(r => r.Reader == Readers.PdfSharp).ToList();
        labels[Readers.PdfSharp] = new
        {
            label = probes.Any(p => p.Positions && p.UnicodeText) ? "see capability records" : "not evaluable",
            licence = LicencePasses(resultsDirectory, "PDFsharp"),
            probes = probes.Count,
            runsOk = sharpRuns.Count(r => r.Class == "ok"),
            runsNotOk = sharpRuns.Where(r => r.Class != "ok").Select(r => $"{r.Pdf}: {r.Class} {r.Reason}").ToList(),
            pagesEqualToReference = probes.Count(p => PdfSet.All.Any(e => File.Exists(ExtractPath(workDirectory, "oracle", Readers.OracleWords, e.Id, 1))
                && ExtractionFile.Read(ExtractPath(workDirectory, "oracle", Readers.OracleWords, e.Id, 1)).PdfSha256 == p.PdfSha256
                && ExtractionFile.Read(ExtractPath(workDirectory, "oracle", Readers.OracleWords, e.Id, 1)).Pages == p.Pages)),
            unicodeTextOnAnyPdf = probes.Any(p => p.UnicodeText),
            positionsOnAnyPdf = probes.Any(p => p.Positions),
            textShowingOperators = probes.Sum(p => p.TextShowingOperators),
            example = probes.FirstOrDefault(p => p.FirstOperandHex is not null),
        };

        Files.WriteJson(Path.Combine(resultsDirectory, "agreement.json"), new { decidedAt = Clock.Now(), tolerance = new { Settings.HorizontalTolerancePt, Settings.VerticalCentreTolerancePt }, agreements, determinism });
        Files.WriteJson(Path.Combine(resultsDirectory, "verdicts.json"), new
        {
            decidedAt = Clock.Now(),
            referenceDecidedAt = oracle["decidedAt"]?.GetValue<string>(),
            firstCandidateRun = candidateRuns.Select(r => r.StartedAt).Order(StringComparer.Ordinal).FirstOrDefault(),
            rows = verdictRows,
        });
        Files.WriteJson(Path.Combine(resultsDirectory, "controls.json"), new { decidedAt = Clock.Now(), detection });
        Files.WriteJson(Path.Combine(resultsDirectory, "observations.json"), new
        {
            note = "Extra observation, never a verdict input: the stamped 'Page X of Y' of the assembled samples, and the text outside its area.",
            stamps = observations,
            pdfsharp = probes,
        });
        Files.WriteJson(Path.Combine(resultsDirectory, "summary.json"), new { decidedAt = Clock.Now(), labels, excluded });
        WriteCost(workDirectory, resultsDirectory);
        foreach (var (k, v) in labels)
        {
            Console.WriteLine($"{k}: {JsonSerializer.Serialize(v, Settings.JsonLine)}");
        }

        return 0;
    }

    private static bool LicencePasses(string resultsDirectory, string package)
    {
        var path = Path.Combine(resultsDirectory, "licences.json");
        if (!File.Exists(path))
        {
            return false;
        }

        var node = JsonNode.Parse(File.ReadAllText(path))!;
        var candidate = node["candidates"]?.AsArray().FirstOrDefault(c => c?["package"]?.GetValue<string>() == package);
        return candidate?["gate"]?.GetValue<string>() == "pass";
    }

    /// <summary>E.4: per page, the stamped "Page i of N" read by the reference, and what the candidate reads in the same area and outside it.</summary>
    private static object Stamps(PdfEntry entry, string configuration, PdfText reference, PdfText candidate)
    {
        var pages = new List<object>();
        var stamped = 0;
        var sameNumber = 0;
        var outsideEqual = 0;
        for (var page = 1; page <= reference.Pages; page++)
        {
            var words = reference.Words.Where(w => w.Page == page).OrderBy(w => w.YCentre).ThenBy(w => w.XMin).ToList();
            List<PdfWord>? stamp = null;
            for (var i = 0; i + 3 < words.Count && stamp is null; i++)
            {
                if (words[i].Text == "Page" && words[i + 1].Text == page.ToString(CultureInfo.InvariantCulture) && words[i + 2].Text == "of"
                    && words[i + 3].Text == reference.Pages.ToString(CultureInfo.InvariantCulture))
                {
                    stamp = words.GetRange(i, 4);
                }
            }

            if (stamp is null)
            {
                continue;
            }

            stamped++;
            var m = Settings.StampAreaMarginPt;
            var (x0, y0, x1, y1) = (stamp.Min(w => w.XMin) - m, stamp.Min(w => w.YMin) - m, stamp.Max(w => w.XMax) + m, stamp.Max(w => w.YMax) + m);
            bool Inside(PdfWord w) => w.XCentre >= x0 && w.XCentre <= x1 && w.YCentre >= y0 && w.YCentre <= y1;
            // Both texts in the checks' own reading order (bands, lines grouped at ±2 pt, left to right), never by raw vertical centre.
            string Text(IEnumerable<PdfWord> ws) => TextNormalisation.N(string.Concat(ws.Select(w => w.Text)));
            PdfText Only(PdfText t, Func<PdfWord, bool> keep) => new() { Pages = t.Pages, PageSizes = t.PageSizes, Words = [.. t.Words.Where(keep)] };
            var expected = Text(Agreement.Ordered(PrintedDocument.From(Only(reference, w => w.Page == page && Inside(w))), page));
            var read = Text(Agreement.Ordered(PrintedDocument.From(Only(candidate, w => w.Page == page && Inside(w))), page));
            var outsideReference = Text(Agreement.Ordered(PrintedDocument.From(Only(reference, w => w.Page == page && !Inside(w))), page));
            var outsideCandidate = Text(Agreement.Ordered(PrintedDocument.From(Only(candidate, w => w.Page == page && !Inside(w))), page));
            var same = expected == read;
            var outside = outsideReference == outsideCandidate;
            sameNumber += same ? 1 : 0;
            outsideEqual += outside ? 1 : 0;
            if (!same || !outside)
            {
                pages.Add(new { page, expected, read, outsideEqual = outside });
            }
        }

        return new { pdf = entry.Id, configuration, stampedPages = stamped, sameStampText = sameNumber, outsideTextEqual = outsideEqual, differences = pages.Take(10) };
    }

    private static void WriteCost(string workDirectory, string resultsDirectory)
    {
        var all = Runs(workDirectory, "oracle").Concat(Runs(workDirectory, "candidates")).ToList();
        var csv = new StringBuilder("reader,pdf,class,layout,pages,runs_ok,runs_not_ok,extract_ms_median,extract_ms_max,ms_per_page_median,ms_per_page_max,tree_peak_rss_kib_max,vmhwm_kib_max,managed_heap_peak_bytes_max,process_wall_ms_max\n");
        foreach (var group in all.GroupBy(r => (r.Reader, r.Pdf)).OrderBy(g => g.Key.Reader, StringComparer.Ordinal).ThenBy(g => g.Key.Pdf, StringComparer.Ordinal))
        {
            var entry = PdfSet.All.First(e => e.Id == group.Key.Pdf);
            var ok = group.Where(r => r.Class == "ok" && r.ExtractMs is not null).ToList();
            var ms = ok.Select(r => r.ExtractMs!.Value).Order().ToList();
            var pages = ok.Select(r => r.Pages).FirstOrDefault();
            string F(double? v) => v is null ? string.Empty : v.Value.ToString("0.###", CultureInfo.InvariantCulture);
            double? Median(List<double> v) => v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
            var median = Median(ms);
            var max = ms.Count == 0 ? (double?)null : ms[^1];
            csv.Append(CultureInfo.InvariantCulture, $"{group.Key.Reader},{group.Key.Pdf},\"{entry.Class}\",{entry.Layout},{pages},{ok.Count},{group.Count() - ok.Count},{F(median)},{F(max)},")
                .Append(CultureInfo.InvariantCulture, $"{F(pages is > 0 ? median / pages : null)},{F(pages is > 0 ? max / pages : null)},")
                .Append(CultureInfo.InvariantCulture, $"{group.Max(r => r.TreePeakRssKiB)},{group.Max(r => r.VmHwmKiB)},{group.Max(r => r.ManagedHeapPeakBytes)},{F(group.Max(r => (double?)r.ProcessWallMs))}\n");
        }

        File.WriteAllText(Path.Combine(resultsDirectory, "cost.csv"), csv.ToString(), new UTF8Encoding(false));
    }
}
