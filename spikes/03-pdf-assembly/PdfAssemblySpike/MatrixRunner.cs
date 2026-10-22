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

    public static int Run(string outDir, string workDir, int repetitions, IReadOnlyList<int> sizes, bool quick, string? fontDir, int batchSize)
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

        // Sets of partial PDFs.
        var manifests = new Dictionary<int, string>();
        var generation = new Dictionary<string, object>();
        foreach (int size in sizes)
        {
            string dir = Path.Combine(workDir, "sets", size.ToString(CultureInfo.InvariantCulture));
            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest))
            {
                var clock = Stopwatch.StartNew();
                PartGenerator.Generate(size, PartGenerator.DefaultSeed, dir);
                generation[$"generateSeconds{size}"] = Math.Round(clock.Elapsed.TotalSeconds, 2);
            }

            manifests[size] = manifest;
            Console.WriteLine($"set {size}: {SetManifest.Load(manifest).Parts.Count} parts, manifest sha256 {SetManifest.Sha256OfFile(manifest)}");
        }

        // Reproducibility, before anything is measured: the 1 000 page set is generated a
        // second time with the same seed and compared part by part with two fields masked.
        string controlsManifest = manifests.TryGetValue(1000, out string? m1000) ? m1000 : EnsureSet(workDir, 1000);
        string repeatDir = Path.Combine(workDir, "sets", "1000-repeat");
        if (Directory.Exists(repeatDir))
        {
            Directory.Delete(repeatDir, true);
        }

        string repeatManifest = PartGenerator.Generate(1000, PartGenerator.DefaultSeed, repeatDir);
        var repro = MaskedComparison.CompareSets(controlsManifest, repeatManifest, Path.Combine(workDir, "repro"));
        Directory.Delete(repeatDir, true);
        PrintRepro(repro);
        if (!repro.Pass)
        {
            const string reproReason = "the two generations of the 1 000 page set are not masked-identical, or a control of the masked comparison did not have its expected verdict";
            ResultsWriter.WriteJson(Path.Combine(outDir, "verification.json"), new
            {
                masks = MaskedComparison.Masks,
                reproducibilityParts = repro.Parts,
                reproducibilityControls = ReproControlsReport(repro),
                structureDetail = repro.StructureDetail,
                outputs = Array.Empty<object>(),
                allPass = false,
                stoppedBeforeMeasuring = reproReason,
            });
            ResultsWriter.WriteJson(Path.Combine(outDir, "summary.json"), new
            {
                started,
                finished = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                quick,
                environment = Program.Versions(),
                reproducibility = MaskedComparison.Summary(repro),
                stoppedBeforeMeasuring = reproReason,
            });
            Console.WriteLine($"matrix stopped before measuring: {reproReason}");
            return 1;
        }

        // Controls of the structural check, on the 1 000 page set, before anything is measured.
        var (controlsPass, controls) = Verifier.RunControls(controlsManifest, Path.Combine(workDir, "controls"));
        foreach (var c in controls)
        {
            Console.WriteLine($"control {c.Id}: expected {c.Expected}, got {c.Actual} (exit {c.ExitCode}, {c.WarningLines} warnings, {c.ExemptWarningLines} exempt{(c.FailReason.Length > 0 ? ", " + c.FailReason : string.Empty)}) {Word(c.AsExpected)}");
        }

        Console.WriteLine($"controls: {Word(controlsPass)}");

        // Structural check of every source part, once per set, before anything is measured.
        var sourceParts = new Dictionary<string, List<SourcePartCheck>>();
        var sourceTotals = new Dictionary<string, SourcePartTotals>();
        bool sourcesPass = true;
        if (controlsPass)
        {
            foreach (int size in sizes)
            {
                var (pass, parts, totals) = Verifier.CheckSourceParts(manifests[size]);
                string key = size.ToString(CultureInfo.InvariantCulture);
                sourceParts[key] = parts;
                sourceTotals[key] = totals;
                sourcesPass &= pass;
                Console.WriteLine($"source parts {size}: {totals.Parts} parts, {totals.PassClean} pass-clean, {totals.PassExempt} pass-exempt, {totals.Fail} fail, warning lines {totals.MinWarningLines}-{totals.MaxWarningLines} (sum {totals.SumWarningLines}) {Word(pass)}");
            }
        }

        if (!controlsPass || !sourcesPass)
        {
            string reason = !controlsPass
                ? "a control of the structural check did not have its expected verdict"
                : "a source part failed the structural check";
            ResultsWriter.WriteJson(Path.Combine(outDir, "verification.json"), new
            {
                exemptMessage = QpdfCheck.ExemptTail,
                masks = MaskedComparison.Masks,
                reproducibilityParts = repro.Parts,
                reproducibilityControls = ReproControlsReport(repro),
                oracleControls = ControlsReport(controlsPass, controls),
                sourceParts,
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
                reproducibility = MaskedComparison.Summary(repro),
                controlsPass,
                sourcePartChecks = sourceTotals,
                stoppedBeforeMeasuring = reason,
            });
            Console.WriteLine($"matrix stopped before measuring: {reason}");
            return 1;
        }

        // Measurements.
        var rows = new List<StepRow>();
        var skipped = new Dictionary<(string, int), string>();
        var fullChecks = new List<(string Pdf, string Manifest, bool Numbered, int Size, string Run)>();
        var verifications = new List<object>();
        var checkedOutputs = new List<VerificationResult>();
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
                        fullChecks.Add((output, manifests[size], variant.Numbered, size, run));
                    }
                    else
                    {
                        var result = Verifier.Verify(manifests[size], output, variant.Numbered, "structure", Path.Combine(workDir, "verify"), outDir);
                        verifications.Add(new { run, variant = variant.Id, size, result });
                        checkedOutputs.Add(result);
                        allPass &= result.Pass;
                        Console.WriteLine($"verify {run} structure: {(result.Pass ? "PASS" : "FAIL " + result.FirstFailure)} ({result.QpdfCheck.Verdict}, {result.QpdfCheck.WarningLines} warnings)");
                        File.Delete(output);
                    }
                }
            }
        }

        // Full verification of the first repetition.
        foreach (var (pdf, manifest, numbered, size, run) in fullChecks)
        {
            var clock = Stopwatch.StartNew();
            string mode = size <= 1000 ? "all" : "sample";
            var result = Verifier.Verify(manifest, pdf, numbered, mode, Path.Combine(workDir, "verify"), outDir);
            verifications.Add(new { run, variant = run[(run.LastIndexOf('-') + 1)..], size, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1), result });
            checkedOutputs.Add(result);
            allPass &= result.Pass;
            Console.WriteLine($"verify {run} {mode}: structure {Word(result.StructurePass)} ({result.QpdfCheck.Verdict}, {result.QpdfCheck.WarningLines} warnings), order {Word(result.OrderPass)}, numbering {Word(result.NumberingPass)}, pixels {Word(result.PixelPass)} ({result.PixelPagesChecked} pages, max outside {result.MaxChangedOutside}, inside {result.MinChangedInside}-{result.MaxChangedInside}){(result.Pass ? string.Empty : " first failure: " + result.FirstFailure)}");
            File.Delete(pdf);
        }

        // Results.
        var summaries = Summarize(rows, sizes, repetitions);
        var bounded = Variants.Select(v => Bounded(v.Id, v.Name, summaries, sizes)).ToList();
        ResultsWriter.WriteRuns(Path.Combine(outDir, "runs.csv"), rows);
        ResultsWriter.WriteJson(Path.Combine(outDir, "verification.json"), new
        {
            exemptMessage = QpdfCheck.ExemptTail,
            masks = MaskedComparison.Masks,
            reproducibilityParts = repro.Parts,
            reproducibilityControls = ReproControlsReport(repro),
            oracleControls = ControlsReport(controlsPass, controls),
            sourceParts,
            samplePages = sizes.Where(s => s > 1000).ToDictionary(s => s.ToString(CultureInfo.InvariantCulture), s => Verifier.SamplePages(SetManifest.Load(manifests[s]))),
            outputs = verifications,
            allPass,
        });
        ResultsWriter.WriteJson(Path.Combine(outDir, "summary.json"), new
        {
            started,
            finished = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            quick,
            repetitions,
            sizes,
            batchSize,
            environment = Program.Versions(),
            seed = PartGenerator.DefaultSeed,
            sets = sizes.ToDictionary(s => s.ToString(CultureInfo.InvariantCulture), s => new
            {
                parts = SetManifest.Load(manifests[s]).Parts.Count,
                minPagesPerPart = SetManifest.Load(manifests[s]).Parts.Min(p => p.Pages),
                maxPagesPerPart = SetManifest.Load(manifests[s]).Parts.Max(p => p.Pages),
                inputBytes = SetManifest.Load(manifests[s]).Parts.Sum(p => new FileInfo(SetManifest.PartPath(manifests[s], p)).Length),
                manifestSha256 = SetManifest.Sha256OfFile(manifests[s]),
                manifestSha256Note = "raw; changes at every generation, not reproducible",
                maskedSetDigest = MaskedComparison.MaskedSetDigest(SetManifest.Load(manifests[s])),
            }),
            generation,
            reproducibility = MaskedComparison.Summary(repro),
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

    /// <summary>The controls block of the masked comparison, for the verification report.</summary>
    public static object ReproControlsReport(ReproResult repro) => new
    {
        allAsExpected = repro.ControlsPass,
        controlFilesUnchanged = repro.ControlFilesUnchanged,
        controls = repro.Controls,
    };

    /// <summary>One console line per control of the masked comparison and one for the set pair.</summary>
    public static void PrintRepro(ReproResult repro)
    {
        foreach (var c in repro.Controls)
        {
            string expected = c.ExpectedFailReason.Length > 0 ? $"{c.ExpectedVerdict} {c.ExpectedFailReason}" : c.ExpectedVerdict;
            string actual = c.FailReason.Length > 0 ? $"{c.Verdict} {c.FailReason}" : c.Verdict;
            Console.WriteLine($"repro control {c.Id} ({c.Group}): expected {expected}, got {actual} (raw differing bytes {c.RawDifferingBytes}, outside spans {c.DifferingBytesOutsideSpans}, tag spans {c.TagSpansA}/{c.TagSpansB}, metadata ID spans {c.XmpSpansA}/{c.XmpSpansB}) {Word(c.AsExpected)}");
        }

        var parts = repro.Parts;
        string range = parts.Count == 0 ? "-" : $"{parts.Min(p => p.RawDifferingBytes)}-{parts.Max(p => p.RawDifferingBytes)}";
        int identical = parts.Count(p => p.Verdict == MaskedComparison.MaskedIdentical);
        Console.WriteLine($"repro {repro.Size}: {identical} of {parts.Count} parts masked-identical, raw differing bytes {range} (recorded, not compared), masked set digests {repro.FirstMaskedSetDigest} / {repro.RepeatMaskedSetDigest}, files match manifests {repro.FilesMatchManifests}, control files unchanged {repro.ControlFilesUnchanged}, controls {Word(repro.ControlsPass)}: {repro.Verdict}");
        foreach (var p in parts.Where(p => p.Verdict != MaskedComparison.MaskedIdentical))
        {
            Console.WriteLine($"repro part {p.Order} {p.File}: {p.FailReason} {p.Detail} {p.BytesAroundA} / {p.BytesAroundB}");
        }
    }

    /// <summary>The controls block of the verification report, with the tolerated message it audits.</summary>
    public static object ControlsReport(bool pass, IReadOnlyList<ControlResult> controls) => new
    {
        exemptMessage = QpdfCheck.ExemptTail,
        closingLine = QpdfCheck.ClosingLine,
        allAsExpected = pass,
        controls,
    };

    private static string EnsureSet(string workDir, int size)
    {
        string dir = Path.Combine(workDir, "sets", size.ToString(CultureInfo.InvariantCulture));
        string manifest = Path.Combine(dir, "manifest.json");
        return File.Exists(manifest) ? manifest : PartGenerator.Generate(size, PartGenerator.DefaultSeed, dir);
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
