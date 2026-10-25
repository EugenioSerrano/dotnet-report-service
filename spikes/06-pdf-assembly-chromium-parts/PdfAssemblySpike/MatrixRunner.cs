using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PdfAssemblySpike;

/// <summary>
/// Runs every size x variant x repetition as fresh processes under GNU time,
/// samples the temporary disk and the output size of each run from outside,
/// verifies the outputs and writes the results.
/// </summary>
internal static class MatrixRunner
{
    public const string TimeProgram = "/usr/bin/time";
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DiskSampleInterval = TimeSpan.FromMilliseconds(100);

    private static readonly (string Id, string Name, bool Numbered)[] Variants =
    [
        ("V1", "pdfsharp-merge", false),
        ("V2", "pdfsharp-merge-stamp", true),
        ("V3", "pdfsharp-staged", true),
        ("V4", "qpdf-merge", false),
        ("V5", "pdfsharp-stamp-qpdf-merge", true),
    ];

    /// <summary>The set produced a second time, compared with the first 1 000 page set before measuring.</summary>
    public const string RepeatSet = "1000-repeat";

    public static int Run(string partsDir, string outDir, string workDir, int repetitions, IReadOnlyList<int> sizes, bool quick, string? fontDir, int batchSize, string datasetPath)
    {
        if (quick)
        {
            sizes = [1000];
            repetitions = 1;
        }

        DejaVuFontResolver.Install(fontDir);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(workDir);
        string started = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // 1. Isolation record at start.
        var isolation = new Dictionary<string, object?>
        {
            ["loadAverageStart"] = ReadText("/proc/loadavg"),
            ["processorCount"] = Environment.ProcessorCount,
            ["memoryMax"] = ReadText("/sys/fs/cgroup/memory.max"),
            ["memorySwapMax"] = ReadText("/sys/fs/cgroup/memory.swap.max"),
            ["cpuMax"] = ReadText("/sys/fs/cgroup/cpu.max"),
            ["hostContainersBefore"] = Environment.GetEnvironmentVariable("HOST_CONTAINERS_BEFORE") ?? "not recorded in-container",
        };
        Console.WriteLine($"isolation: load average {isolation["loadAverageStart"]}, {Environment.ProcessorCount} CPUs, memory.max {isolation["memoryMax"]}, swap {isolation["memorySwapMax"]}, cpu.max {isolation["cpuMax"]}");

        // 2. The sets copied into the container's own writable layer, every part's hash checked.
        var setNames = sizes.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList();
        if (!setNames.Contains("1000"))
        {
            setNames.Insert(0, "1000");
        }

        setNames.Add(RepeatSet);
        var manifestsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        var production = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string name in setNames)
        {
            string source = Path.Combine(partsDir, name);
            string target = Path.Combine(workDir, "sets", name);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }

            manifestsByName[name] = Path.Combine(target, "manifest.json");
            string productionFile = Path.Combine(source, "production.json");
            if (File.Exists(productionFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(productionFile));
                production[name] = document.RootElement.Clone();
            }
        }

        ResultsWriter.WriteJson(Path.Combine(outDir, "production.json"), production);
        var manifests = sizes.ToDictionary(s => s, s => manifestsByName[s.ToString(CultureInfo.InvariantCulture)]);
        var afterCopy = InputIdentity(manifestsByName);
        Console.WriteLine($"inputs after copy: {afterCopy.Parts} parts, {afterCopy.Mismatches} hash mismatches {Word(afterCopy.Mismatches == 0)}");
        foreach (var (name, manifest) in manifestsByName)
        {
            Console.WriteLine($"set {name}: {SetManifest.Load(manifest).Parts.Count} parts, manifest sha256 {SetManifest.Sha256OfFile(manifest)}");
        }

        var context = new StopContext(outDir, started, quick, isolation, afterCopy);
        if (afterCopy.Mismatches > 0)
        {
            return context.Stop("a part's SHA-256 after the copy differs from its manifest value");
        }

        // 3. Fixture checks on every set. First the input rows, read from the dataset and
        // each manifest: only ordinary records used, contiguous slices of them, the
        // exclusion as the dataset shows it.
        var dataset = DatasetRows.Load(datasetPath);
        var inputRows = new Dictionary<string, InputRowCheck>(StringComparer.Ordinal);
        foreach (var (name, manifest) in manifestsByName)
        {
            var rowCheck = Verifier.CheckInputRows(name, manifest, dataset);
            inputRows[name] = rowCheck;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"input rows {name}: {rowCheck.RecordsUsed} ordinary records used in {rowCheck.Parts} parts (sequences {rowCheck.UsedFirstSequence}-{rowCheck.UsedLastSequence}, {rowCheck.ExcludedInUsedRange} left out in that range, manifest {rowCheck.ManifestExcludedInUsedRange}), longest used description {rowCheck.LongestUsedDescription}, dataset excludes {rowCheck.ExcludedInDataset} (expected {rowCheck.ExpectedExcluded}, manifest {rowCheck.ManifestExcludedCount}), first ordinary {rowCheck.FirstOrdinarySequence} {Word(rowCheck.Pass)}{(rowCheck.Pass ? string.Empty : " — " + rowCheck.FirstFailure)}"));
        }

        context.InputRows = inputRows;
        if (inputRows.Values.Any(r => !r.Pass))
        {
            return context.Stop("an input-row check failed (stop and ask)");
        }

        // Then page boxes and the rounded copies, numbering area empty on both
        // references, pages unique, own counter, bottom band, content digests.
        var fixtures = new Dictionary<string, FixtureCheck>(StringComparer.Ordinal);
        foreach (var (name, manifest) in manifestsByName)
        {
            var clock = Stopwatch.StartNew();
            var fixture = Verifier.CheckFixture(name, manifest, Path.Combine(workDir, "fixture"), RoundedDir(workDir, name));
            fixtures[name] = fixture;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"fixture {name}: {fixture.Pages} pages, page boxes {Word(fixture.BoxesPass)} ({string.Join(", ", fixture.SourceBoxValues.Select(b => b.Box + " x" + b.Pages))}; {fixture.RoundedCopies.Count(c => c.Pass)} of {fixture.RoundedCopies.Count} rounded copies built), area empty {Word(fixture.AreaEmpty)} (words {fixture.PagesWithWordInArea}, pixels {fixture.PixelPagesNotWhite} not white on {fixture.PixelPagesChecked} pages x 2 references, {fixture.RoundedPixelPagesNotWhite} of them on the rounded copy), pages unique {Word(fixture.PagesUnique)}, own counter {Word(fixture.OwnCounter)}, bottom band holds words on {fixture.BottomBandPages} pages ({fixture.BottomBandDistinctPatterns} patterns), content set digest {fixture.ContentSetDigest}, {clock.Elapsed.TotalSeconds:F1} s"));
            if (fixture.FirstWordInArea is not null || fixture.FirstNotWhitePage is not null)
            {
                Console.WriteLine($"  numbering area occupied: {fixture.FirstWordInArea ?? fixture.FirstNotWhitePage}");
            }

            if (fixture.FirstBoxFailure is not null)
            {
                Console.WriteLine($"  page box check failed: {fixture.FirstBoxFailure}");
            }
        }

        context.Fixtures = fixtures;
        if (fixtures.Values.Any(f => !f.BoxesPass))
        {
            return context.Stop("a source page box or the construction of a rounded copy failed its checks (stop and ask)");
        }

        if (fixtures.Values.Any(f => !f.AreaEmpty))
        {
            return context.Stop("the numbering area is not empty on some source page");
        }

        if (fixtures.Values.Any(f => !f.Pass))
        {
            return context.Stop("a fixture check failed (pages not unique or own page counter not as expected)");
        }

        // 4. Reproducibility across two generations of the 1 000 page set, with its controls.
        var repro = ChromiumInputs.CompareSets(manifestsByName["1000"], manifestsByName[RepeatSet], Path.Combine(workDir, "repro"));
        PrintRepro(repro);
        context.Repro = repro;
        if (!repro.Pass)
        {
            return context.Stop("the two generations of the 1 000 page set are not content-identical, or a control of the comparison did not have its expected result");
        }

        // 5. The 29 counted controls: structural check (PDF library and Chromium groups) and page-box rule.
        string fixturePath = Program.DefaultFixture;
        var (controlsPass, controls) = Verifier.RunControls(manifestsByName["1000"], fixturePath, Path.Combine(workDir, "controls"));
        foreach (var c in controls)
        {
            Console.WriteLine($"control {c.Group} {c.Id}: expected {c.Expected}, got {c.Actual} (exit {c.ExitCode}, {c.WarningLines} warnings, {c.ExemptWarningLines} exempt{(c.FailReason.Length > 0 ? ", " + c.FailReason : string.Empty)}) {Word(c.AsExpected)}");
        }

        Console.WriteLine($"controls: {controls.Count(c => c.Counted && c.AsExpected)} of {controls.Count(c => c.Counted)} counted controls as expected {Word(controlsPass)}; recorded only: {string.Join(", ", controls.Where(c => !c.Counted).Select(c => c.Id + " " + c.Actual))}");
        context.Controls = ControlsReport(controlsPass, controls, fixturePath);
        context.ControlsPass = controlsPass;
        if (!controlsPass)
        {
            return context.Stop("a counted control did not have its expected result");
        }

        // 6. Structural check of every source part, once per set.
        var sourceParts = new Dictionary<string, List<SourcePartCheck>>();
        var sourceTotals = new Dictionary<string, SourcePartTotals>();
        bool sourcesPass = true;
        foreach (var (name, manifest) in manifestsByName)
        {
            var (pass, parts, totals) = Verifier.CheckSourceParts(manifest);
            sourceParts[name] = parts;
            sourceTotals[name] = totals;
            sourcesPass &= pass;
            Console.WriteLine($"source parts {name}: {totals.Parts} parts, {totals.PassClean} pass-clean, {totals.PassExempt} pass-exempt, {totals.Fail} fail, warning lines {totals.MinWarningLines}-{totals.MaxWarningLines} (sum {totals.SumWarningLines}) {Word(pass)}");
        }

        context.SourceParts = sourceParts;
        context.SourceTotals = sourceTotals;
        if (!sourcesPass)
        {
            return context.Stop("a source part failed the structural check");
        }

        // 7. Measurements.
        var rows = new List<StepRow>();
        var skipped = new Dictionary<(string, int), string>();
        var fullChecks = new List<(string Pdf, string Manifest, bool Numbered, int Size, string Run, string Variant)>();
        var verifications = new List<object>();
        var checkedOutputs = new List<VerificationResult>();
        var boxRuns = new List<(string Run, string Variant, int Size, VerificationResult Result)>();
        bool allPass = repro.Pass;
        for (int rep = 1; rep <= repetitions; rep++)
        {
            foreach (int size in sizes)
            {
                foreach (var variant in Variants)
                {
                    if (skipped.TryGetValue((variant.Id, size), out string? reason))
                    {
                        rows.Add(Skipped(variant, size, manifests[size], rep, reason));
                        continue;
                    }

                    string run = string.Create(CultureInfo.InvariantCulture, $"r{rep}-{size}-{variant.Id}");
                    var (steps, output) = RunCombination(variant.Id, variant.Name, size, rep, run, manifests[size], workDir, batchSize);
                    rows.AddRange(steps);
                    foreach (var s in steps)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{s.Variant} {s.Size} r{s.Repetition} {s.Step}: {s.Outcome} exit {s.Exit}, {s.WallSeconds:F2} s, RSS {s.PeakRssKb / 1024.0:F1} MiB, heap {(s.ManagedHeapPeakBytes ?? 0) / 1048576.0:F1} MiB, temp {s.TempDiskPeakBytes / 1048576.0:F1} MiB, out {s.OutputBytes / 1048576.0:F1} MiB"));
                    }

                    var failed = steps.FirstOrDefault(s => s.Outcome != "ok");
                    if (failed is not null)
                    {
                        skipped[(variant.Id, size)] = $"skipped after {failed.Outcome} in repetition {rep}";
                        if (File.Exists(output))
                        {
                            File.Delete(output);
                        }

                        continue;
                    }

                    if (rep == 1)
                    {
                        fullChecks.Add((output, manifests[size], variant.Numbered, size, run, variant.Id));
                    }
                    else
                    {
                        var result = Verifier.Verify(manifests[size], output, variant.Numbered, "structure", Path.Combine(workDir, "verify"), outDir,
                            PageBoxes.RuleFor(variant.Id), RoundedDir(workDir, size.ToString(CultureInfo.InvariantCulture)));
                        verifications.Add(new { run, variant = variant.Id, size, result });
                        checkedOutputs.Add(result);
                        boxRuns.Add((run, variant.Id, size, result));
                        allPass &= result.Pass;
                        Console.WriteLine($"verify {run} structure: {(result.Pass ? "PASS" : "FAIL " + result.FirstFailure)} ({result.QpdfCheck.Verdict}, {result.QpdfCheck.WarningLines} warnings; page boxes {result.Boxes.Verdict}: {string.Join(", ", result.Boxes.Values.Select(b => b.Box + " x" + b.Pages))})");
                        File.Delete(output);
                    }
                }
            }
        }

        // 8. Full verification of the first repetition.
        foreach (var (pdf, manifest, numbered, size, run, variantId) in fullChecks)
        {
            var clock = Stopwatch.StartNew();
            string mode = size <= 1000 ? "all" : "sample";
            var result = Verifier.Verify(manifest, pdf, numbered, mode, Path.Combine(workDir, "verify"), outDir,
                PageBoxes.RuleFor(variantId), RoundedDir(workDir, size.ToString(CultureInfo.InvariantCulture)));
            verifications.Add(new { run, variant = variantId, size, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), result });
            checkedOutputs.Add(result);
            boxRuns.Add((run, variantId, size, result));
            allPass &= result.Pass;
            Console.WriteLine($"verify {run} {mode}: structure {Word(result.StructurePass)} ({result.QpdfCheck.Verdict}, {result.QpdfCheck.WarningLines} warnings), page boxes {Word(result.Boxes.Pass)} ({result.Boxes.Verdict}: {string.Join(", ", result.Boxes.Values.Select(b => b.Box + " x" + b.Pages))}), order {Word(result.OrderPass)}, numbering {Word(result.NumberingPass)}, pixels {Word(result.PixelPass)} ({result.PixelPagesChecked} pages against the matching reference, max outside {result.MaxChangedOutside}, inside {result.MinChangedInside}-{result.MaxChangedInside}; against the unedited source, information only: outside {result.Strict.MinOutside}-{result.Strict.MaxOutside}, total {result.Strict.SumOutside}){(result.Pass ? string.Empty : " first failure: " + result.FirstFailure)}");
            File.Delete(pdf);
        }

        // 9. Input identity at the end, and the load average. The rounded copies, whose
        // hashes the fixture checks recorded, are deleted.
        var atEnd = InputIdentity(manifestsByName);
        allPass &= atEnd.Mismatches == 0;
        isolation["loadAverageEnd"] = ReadText("/proc/loadavg");
        Console.WriteLine($"inputs at end: {atEnd.Parts} parts, {atEnd.Mismatches} hash mismatches {Word(atEnd.Mismatches == 0)}; load average {isolation["loadAverageEnd"]}");
        string roundedRoot = Path.Combine(workDir, "ref-rounded");
        if (Directory.Exists(roundedRoot))
        {
            Directory.Delete(roundedRoot, recursive: true);
        }

        Console.WriteLine($"rounded copies deleted: {!Directory.Exists(roundedRoot)}");

        // Results.
        var summaries = Summarize(rows, sizes, repetitions);
        var bounded = Variants.Select(v => Bounded(v.Id, v.Name, summaries, sizes)).ToList();
        ResultsWriter.WriteRuns(Path.Combine(outDir, "runs.csv"), rows);
        ResultsWriter.WriteJson(Path.Combine(outDir, "verification.json"), new
        {
            exemptMessage = QpdfCheck.ExemptTail,
            numberingArea = fixtures["1000"].Area,
            numberingAreaPixels = PixelRect.NumberingArea.ToString(),
            inputRowChecks = inputRows,
            fixtureChecks = fixtures,
            reproducibilityChromium = repro.Parts,
            reproducibilityControlsChromium = new { allAsExpected = repro.ControlsPass, controls = repro.Controls },
            oracleControls = context.Controls,
            pageBoxes = PageBoxReport(fixtures, boxRuns),
            sourceParts,
            samplePages = sizes.Where(s => s > 1000).ToDictionary(s => s.ToString(CultureInfo.InvariantCulture), s => Verifier.SamplePages(SetManifest.Load(manifests[s]))),
            outputs = verifications,
            allPass,
        });
        var environment = Program.Versions();
        environment["loadAverageStart"] = isolation["loadAverageStart"];
        environment["loadAverageEnd"] = isolation["loadAverageEnd"];
        environment["hostContainersBefore"] = isolation["hostContainersBefore"];
        ResultsWriter.WriteJson(Path.Combine(outDir, "summary.json"), new
        {
            started,
            finished = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            quick,
            repetitions,
            sizes,
            batchSize,
            environment,
            isolation,
            sets = sizes.ToDictionary(s => s.ToString(CultureInfo.InvariantCulture), s => SetSummary(manifests[s], fixtures[s.ToString(CultureInfo.InvariantCulture)])),
            repeatSet = SetSummary(manifestsByName[RepeatSet], fixtures[RepeatSet]),
            reproducibility = ReproSummary(repro, afterCopy, atEnd),
            inputRows = inputRows.ToDictionary(r => r.Key, r => InputRowSummary(r.Value)),
            fixture = fixtures.ToDictionary(f => f.Key, f => FixtureSummary(f.Value)),
            controlsPass,
            sourcePartChecks = sourceTotals,
            outputChecks = OutputCheckTotals(checkedOutputs),
            combinations = summaries,
            bounded,
            allVerificationsPass = allPass,
        });

        Console.WriteLine($"matrix finished: verification {(allPass ? "PASS" : "FAIL")}");
        return allPass ? 0 : 1;
    }

    private static string Word(bool pass) => pass ? "PASS" : "FAIL";

    /// <summary>The folder of a set's rounded copies, inside the container's temporary work folder.</summary>
    private static string RoundedDir(string workDir, string setName) => Path.Combine(workDir, "ref-rounded", setName);

    /// <summary>
    /// The page boxes of every source part and every output: the distinct values as
    /// qpdf wrote them with their page counts and verdicts, the rounded copies' hashes,
    /// and per checked page the reference used with both pixel counts.
    /// </summary>
    private static object PageBoxReport(Dictionary<string, FixtureCheck> fixtures, List<(string Run, string Variant, int Size, VerificationResult Result)> runs) => new
    {
        sourceBox = "[" + PageBoxes.SourceBoxText + "]",
        roundedBox = "[" + PageBoxes.RoundedBoxText + "]",
        rules = new Dictionary<string, string>
        {
            ["V4"] = PageBoxes.SourceOnlyRule,
            ["V1, V2, V3, V5"] = PageBoxes.SourceOrRoundedRule,
        },
        sources = fixtures.ToDictionary(f => f.Key, f => new
        {
            pass = f.Value.BoxesPass,
            values = f.Value.SourceBoxValues,
            roundedCopies = f.Value.RoundedCopies.Select(c => new
            {
                c.Order,
                c.File,
                c.Pages,
                c.MediaBoxCount,
                c.SourceBoxesAsRead,
                c.CopyBoxesAsRead,
                c.DifferingBytes,
                c.DifferencesInsideBoxNumbers,
                c.Bytes,
                c.CopyBytes,
                c.SourceCheck,
                c.CopyCheck,
                c.CopySha256,
                c.Pass,
                c.Failure,
            }),
        }),
        outputs = runs.Select(r => new
        {
            run = r.Run,
            variant = r.Variant,
            size = r.Size,
            mode = r.Result.Mode,
            boxes = r.Result.Boxes,
            strictAgainstUnedited = r.Result.Strict,
            checkedPages = r.Result.PixelPageRecords,
        }),
    };

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    /// <summary>Every part's raw SHA-256 recomputed and compared with its manifest value.</summary>
    private static InputIdentityResult InputIdentity(Dictionary<string, string> manifests)
    {
        int parts = 0;
        int mismatches = 0;
        string? first = null;
        foreach (var (name, manifestPath) in manifests)
        {
            foreach (var part in SetManifest.Load(manifestPath).Parts)
            {
                parts++;
                string actual = SetManifest.Sha256OfFile(SetManifest.PartPath(manifestPath, part));
                if (!string.Equals(actual, part.Sha256, StringComparison.Ordinal))
                {
                    mismatches++;
                    first ??= $"{name} part {part.Order} ({part.File}): {actual}, manifest {part.Sha256}";
                }
            }
        }

        return new InputIdentityResult(parts, mismatches, first);
    }

    /// <summary>Per set: shape, sizes, producer and dataset as the manifest states them, and the content digest.</summary>
    private static object SetSummary(string manifestPath, FixtureCheck fixture)
    {
        var manifest = SetManifest.Load(manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        long inputBytes = manifest.Parts.Sum(p => new FileInfo(SetManifest.PartPath(manifestPath, p)).Length);
        return new
        {
            parts = manifest.Parts.Count,
            minPagesPerPart = manifest.Parts.Min(p => p.Pages),
            maxPagesPerPart = manifest.Parts.Max(p => p.Pages),
            inputBytes,
            bytesPerPage = Math.Round((double)inputBytes / manifest.TargetPages, 1),
            manifestSha256 = SetManifest.Sha256OfFile(manifestPath),
            manifestSha256Note = "raw; carries the parts' raw hashes, which change with the print time",
            contentSetDigest = fixture.ContentSetDigest,
            producer = document.RootElement.GetProperty("producer").Clone(),
            dataset = document.RootElement.GetProperty("dataset").Clone(),
            recordsUsed = document.RootElement.GetProperty("parts").EnumerateArray().Sum(e => e.GetProperty("records").GetInt32()),
        };
    }

    private static object InputRowSummary(InputRowCheck r) => new
    {
        r.Pass,
        r.RecordsUsed,
        r.UsedFirstSequence,
        r.UsedLastSequence,
        r.ExcludedInUsedRange,
        r.LongestUsedDescription,
        r.ExcludedInDataset,
        r.Threshold,
        r.LargestBelow,
        r.SmallestAtOrAbove,
        r.FirstFailure,
    };

    private static object FixtureSummary(FixtureCheck f) => new
    {
        f.Pages,
        f.AreaEmpty,
        f.PagesWithWordInArea,
        f.PixelPagesChecked,
        f.PixelPagesNotWhite,
        f.PagesUnique,
        f.OwnCounter,
        f.BottomBandPages,
        f.BottomBandDistinctPatterns,
        f.BottomBandPatterns,
        f.ContentSetDigest,
        f.BoxesPass,
        f.SourceBoxValues,
        f.RoundedPixelPagesNotWhite,
        roundedCopiesBuilt = f.RoundedCopies.Count(c => c.Pass),
    };

    private static object ReproSummary(ChromiumRepro repro, InputIdentityResult afterCopy, InputIdentityResult atEnd) => new
    {
        withinRun = new
        {
            parts = afterCopy.Parts,
            mismatchesAfterCopy = afterCopy.Mismatches,
            mismatchesAtEnd = atEnd.Mismatches,
            firstMismatch = afterCopy.FirstMismatch ?? atEnd.FirstMismatch,
            identical = afterCopy.Mismatches == 0 && atEnd.Mismatches == 0,
        },
        acrossGenerations = new
        {
            size = repro.Size,
            verdict = repro.Verdict,
            pairs = repro.Parts.Count,
            contentIdentical = repro.Parts.Count(p => p.Verdict == ChromiumInputs.ContentIdentical),
            firstContentSetDigest = repro.FirstContentSetDigest,
            repeatContentSetDigest = repro.RepeatContentSetDigest,
            classifications = repro.Parts.GroupBy(p => p.Classification).ToDictionary(g => g.Key, g => g.Count()),
            rawDifferingBytesMin = repro.Parts.Count == 0 ? 0 : repro.Parts.Min(p => p.RawDifferingBytes),
            rawDifferingBytesMax = repro.Parts.Count == 0 ? 0 : repro.Parts.Max(p => p.RawDifferingBytes),
            rawBytesNote = "recorded and classified, never a pass condition; nothing is masked",
            controlsPass = repro.ControlsPass,
        },
    };

    /// <summary>One console line per control of the comparison and one for the set pair.</summary>
    public static void PrintRepro(ChromiumRepro repro)
    {
        foreach (var c in repro.Controls)
        {
            string expected = c.ExpectedClassification.Length > 0 ? $"{c.ExpectedVerdict} / {c.ExpectedClassification}" : c.ExpectedVerdict;
            Console.WriteLine($"repro control {c.Id}: expected {expected}, got {c.Verdict} / {c.Classification} (raw differing bytes {c.RawDifferingBytes}) {Word(c.AsExpected)}");
        }

        var parts = repro.Parts;
        string range = parts.Count == 0 ? "-" : $"{parts.Min(p => p.RawDifferingBytes)}-{parts.Max(p => p.RawDifferingBytes)}";
        int identical = parts.Count(p => p.Verdict == ChromiumInputs.ContentIdentical);
        string classes = string.Join(", ", parts.GroupBy(p => p.Classification).Select(g => $"{g.Key} {g.Count()}"));
        Console.WriteLine($"repro {repro.Size}: {identical} of {parts.Count} parts content-identical, raw differing bytes {range} ({classes}; recorded, not compared), content set digests {repro.FirstContentSetDigest} / {repro.RepeatContentSetDigest}, controls {Word(repro.ControlsPass)}: {repro.Verdict}");
        foreach (var p in parts.Where(p => p.Classification == ChromiumInputs.Other).Take(3))
        {
            Console.WriteLine($"repro part {p.Order} {p.File}: other bytes at {p.FirstOtherOffset}: '{p.BytesAroundA}' / '{p.BytesAroundB}'");
        }
    }

    /// <summary>The controls block of the verification report, with the tolerated message it audits.</summary>
    public static object ControlsReport(bool pass, IReadOnlyList<ControlResult> controls, string fixturePath) => new
    {
        exemptMessage = QpdfCheck.ExemptTail,
        closingLine = QpdfCheck.ClosingLine,
        pdfsharpFixture = fixturePath,
        pdfsharpFixtureSha256 = File.Exists(fixturePath) ? SetManifest.Sha256OfFile(fixturePath) : null,
        pdfsharpFixtureBytes = File.Exists(fixturePath) ? new FileInfo(fixturePath).Length : (long?)null,
        allAsExpected = pass,
        groups = controls.GroupBy(c => c.Group).ToDictionary(g => g.Key, g => new { count = g.Count(), asExpected = g.Count(c => c.AsExpected) }),
        controls,
    };

    /// <summary>Whether the parts kept their bytes: the count checked and the hash mismatches.</summary>
    private sealed record InputIdentityResult(int Parts, int Mismatches, string? FirstMismatch);

    /// <summary>What is known when the matrix stops before measuring, and how it is written.</summary>
    private sealed class StopContext(string outDir, string started, bool quick, Dictionary<string, object?> isolation, InputIdentityResult afterCopy)
    {
        public Dictionary<string, InputRowCheck>? InputRows { get; set; }

        public Dictionary<string, FixtureCheck>? Fixtures { get; set; }

        public ChromiumRepro? Repro { get; set; }

        public object? Controls { get; set; }

        public bool? ControlsPass { get; set; }

        public Dictionary<string, List<SourcePartCheck>>? SourceParts { get; set; }

        public Dictionary<string, SourcePartTotals>? SourceTotals { get; set; }

        public int Stop(string reason)
        {
            isolation["loadAverageEnd"] = ReadText("/proc/loadavg");
            ResultsWriter.WriteJson(Path.Combine(outDir, "verification.json"), new
            {
                exemptMessage = QpdfCheck.ExemptTail,
                inputRowChecks = InputRows,
                fixtureChecks = Fixtures,
                reproducibilityChromium = Repro?.Parts,
                reproducibilityControlsChromium = Repro is null ? null : new { allAsExpected = Repro.ControlsPass, controls = Repro.Controls },
                oracleControls = Controls,
                sourceParts = SourceParts,
                outputs = Array.Empty<object>(),
                allPass = false,
                stoppedBeforeMeasuring = reason,
            });
            ResultsWriter.WriteJson(Path.Combine(outDir, "summary.json"), new
            {
                started,
                finished = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                quick,
                environment = Program.Versions(),
                isolation,
                inputsAfterCopy = afterCopy,
                inputRows = InputRows?.ToDictionary(r => r.Key, r => InputRowSummary(r.Value)),
                fixture = Fixtures?.ToDictionary(f => f.Key, f => FixtureSummary(f.Value)),
                reproducibility = Repro is null ? null : new { Repro.Verdict, Repro.ControlsPass, Repro.FirstContentSetDigest, Repro.RepeatContentSetDigest },
                controlsPass = ControlsPass,
                sourcePartChecks = SourceTotals,
                stoppedBeforeMeasuring = reason,
            });
            Console.WriteLine($"matrix stopped before measuring: {reason}");
            return 1;
        }
    }

    /// <summary>Structural check totals over the verified outputs, per verdict.</summary>
    private static object OutputCheckTotals(List<VerificationResult> results) => new
    {
        outputs = results.Count,
        passClean = results.Count(r => r.QpdfCheck.Verdict == QpdfCheck.PassClean),
        passExempt = results.Count(r => r.QpdfCheck.Verdict == QpdfCheck.PassExempt),
        fail = results.Count(r => !r.QpdfCheck.Pass),
        minWarningLines = results.Count == 0 ? 0 : results.Min(r => r.QpdfCheck.WarningLines),
        maxWarningLines = results.Count == 0 ? 0 : results.Max(r => r.QpdfCheck.WarningLines),
    };

    private static (List<StepRow> Steps, string Output) RunCombination(string id, string name, int size, int rep, string run, string manifestPath, string workDir, int batchSize)
    {
        var manifest = SetManifest.Load(manifestPath);
        string outDir = Path.Combine(workDir, "out");
        string tempDir = Path.Combine(workDir, "tmp", run);
        string timeDir = Path.Combine(workDir, "time");
        string argsDir = Path.Combine(workDir, "args");
        foreach (string dir in new[] { outDir, tempDir, timeDir, argsDir })
        {
            Directory.CreateDirectory(dir);
        }

        string output = Path.Combine(outDir, run + ".pdf");
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        var steps = new List<StepRow>();
        string dotnet = Environment.ProcessPath ?? "dotnet";
        string dll = typeof(MatrixRunner).Assembly.Location;
        try
        {
            switch (id)
            {
                case "V1":
                case "V2":
                case "V3":
                    steps.Add(Measure(id, name, size, manifest.Parts.Count, rep, "single", run, tempDir, output,
                        [dotnet, dll, "assemble", "--variant", name, "--manifest", manifestPath, "--out", output, "--temp", tempDir,
                            "--metrics", Path.Combine(timeDir, run + ".metrics.json"), "--batch", batchSize.ToString(CultureInfo.InvariantCulture)],
                        Path.Combine(timeDir, run + ".metrics.json")));
                    break;
                case "V4":
                    {
                        string args = Path.Combine(argsDir, run + ".args");
                        Assembler.WriteQpdfArguments(manifest, manifestPath, null, output, args);
                        steps.Add(Measure(id, name, size, manifest.Parts.Count, rep, "single", run, tempDir, output, [Verifier.Qpdf, "@" + args], null));
                        break;
                    }

                default:
                    {
                        string metrics = Path.Combine(timeDir, run + ".a.metrics.json");
                        var a = Measure(id, name, size, manifest.Parts.Count, rep, "a-stamp-parts", run, tempDir, null,
                            [dotnet, dll, "assemble", "--variant", Assembler.StampParts, "--manifest", manifestPath, "--out", output, "--temp", tempDir,
                            "--metrics", metrics], metrics);
                        steps.Add(a);
                        if (a.Outcome == "ok")
                        {
                            string args = Path.Combine(argsDir, run + ".args");
                            Assembler.WriteQpdfArguments(manifest, manifestPath, tempDir, output, args);
                            steps.Add(Measure(id, name, size, manifest.Parts.Count, rep, "b-qpdf-merge", run, tempDir, output, [Verifier.Qpdf, "@" + args], null));
                        }

                        break;
                    }
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }

        return (steps, output);
    }

    private static StepRow Measure(string id, string name, int size, int parts, int rep, string step, string run, string tempDir, string? output, string[] command, string? metricsPath)
    {
        string timeFile = Path.Combine(Path.GetDirectoryName(tempDir)!, "..", "time", $"{run}.{step}.time");
        long tempPeak = 0;
        long outputPeak = 0;
        void Sample()
        {
            tempPeak = Math.Max(tempPeak, DirectorySize(tempDir));
            if (output is not null)
            {
                outputPeak = Math.Max(outputPeak, FileSize(output));
            }
        }

        var arguments = new List<string> { "-v", "-o", timeFile };
        arguments.AddRange(command);
        var result = ExternalTool.Run(TimeProgram, arguments, StepTimeout, Sample, DiskSampleInterval);
        var time = File.Exists(timeFile) ? TimeOutput.Parse(timeFile) : new TimeOutput(0, 0, 0, 0, null, null);
        ProcessMetrics? metrics = metricsPath is not null && File.Exists(metricsPath)
            ? JsonSerializer.Deserialize<ProcessMetrics>(File.ReadAllText(metricsPath), SetManifest.Json)
            : null;
        string outcome = result.TimedOut ? "timeout"
            : result.ExitCode == 0 ? "ok"
            : result.ExitCode == 137 || time.Signal == 9 ? "oom-killed"
            : "error";
        string? reason = outcome == "ok" ? null
            : FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput) ?? $"exit {result.ExitCode}";
        long outputBytes = output is not null ? FileSize(output) : metrics?.OutputBytes ?? 0;
        return new StepRow(id, name, size, parts, rep, step, outcome, result.ExitCode, time.WallSeconds, time.UserSeconds, time.SystemSeconds,
            time.MaxRssKb, metrics is null ? null : metrics.VmHwmBytes / 1024, metrics?.ManagedHeapPeakBytes, tempPeak, outputBytes,
            metrics?.Gen0Collections, metrics?.Gen1Collections, metrics?.Gen2Collections, reason);
    }

    private static StepRow Skipped((string Id, string Name, bool Numbered) variant, int size, string manifestPath, int rep, string reason) =>
        new(variant.Id, variant.Name, size, SetManifest.Load(manifestPath).Parts.Count, rep, "single", "skipped", -1, 0, 0, 0, 0, null, null, 0, 0, null, null, null, reason);

    private static List<CombinationSummary> Summarize(List<StepRow> rows, IReadOnlyList<int> sizes, int repetitions)
    {
        var list = new List<CombinationSummary>();
        foreach (int size in sizes)
        {
            foreach (var variant in Variants)
            {
                var mine = rows.Where(r => r.Variant == variant.Id && r.Size == size).ToList();
                var reps = mine.GroupBy(r => r.Repetition)
                    .Where(g => g.All(r => r.Outcome == "ok") && (variant.Id != "V5" || g.Count() == 2))
                    .Select(g => new
                    {
                        Rss = g.Max(r => r.PeakRssKb) / 1024.0,
                        Heap = g.Select(r => r.ManagedHeapPeakBytes).FirstOrDefault(h => h is not null),
                        Wall = g.Sum(r => r.WallSeconds),
                        Temp = g.Max(r => r.TempDiskPeakBytes) / 1048576.0,
                        Output = g.Last().OutputBytes / 1048576.0,
                    })
                    .ToList();
                var limits = mine.Where(r => r.Outcome != "ok")
                    .Select(r => $"repetition {r.Repetition} {r.Step}: {r.Outcome} — {r.Reason}").ToList();
                list.Add(new CombinationSummary(variant.Id, variant.Name, size, repetitions, reps.Count, limits,
                    ResultsWriter.Of(reps.Select(r => r.Rss)),
                    ResultsWriter.Of(reps.Where(r => r.Heap is not null).Select(r => r.Heap!.Value / 1048576.0)),
                    ResultsWriter.Of(reps.Select(r => r.Wall)),
                    ResultsWriter.Of(reps.Select(r => r.Temp)),
                    ResultsWriter.Of(reps.Select(r => r.Output))));
            }
        }

        return list;
    }

    /// <summary>Bounded when the median peak RSS at 10 000 pages is at most 1.5 times the median at 1 000 pages.</summary>
    private static object Bounded(string id, string name, List<CombinationSummary> summaries, IReadOnlyList<int> sizes)
    {
        var small = summaries.FirstOrDefault(s => s.Variant == id && s.Size == 1000);
        var large = summaries.FirstOrDefault(s => s.Variant == id && s.Size == 10000);
        if (small is null || large is null || !sizes.Contains(10000))
        {
            return new { variant = id, name, verdict = "not evaluated", reason = "the 1 000 and 10 000 page sizes were not both measured" };
        }

        if (large.Limits.Count > 0 || large.PeakRssMiB.Median is null || small.PeakRssMiB.Median is null)
        {
            return new { variant = id, name, verdict = "not bounded", reason = "a 10 000 page run failed or was not measured" };
        }

        double ratio = large.PeakRssMiB.Median.Value / small.PeakRssMiB.Median.Value;
        double? heapRatio = large.ManagedHeapPeakMiB.Median is not null && small.ManagedHeapPeakMiB.Median is > 0
            ? Math.Round(large.ManagedHeapPeakMiB.Median.Value / small.ManagedHeapPeakMiB.Median.Value, 2)
            : null;
        return new
        {
            variant = id,
            name,
            verdict = ratio <= 1.5 ? "bounded" : "not bounded",
            rssRatio10000To1000 = Math.Round(ratio, 2),
            heapRatio10000To1000 = heapRatio,
        };
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                    // The file was removed while it was being counted.
                }
            }
        }
        catch (IOException)
        {
            // The folder was removed while it was being counted.
        }

        return total;
    }

    private static long FileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string? FirstLine(string text)
    {
        string? line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? null : line;
    }
}
