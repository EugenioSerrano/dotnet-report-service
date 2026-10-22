using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XlsxWriterSpike.Measurement;

namespace XlsxWriterSpike.Suite;

/// <summary>
/// Repeatable measurements: one child process per run (so every memory figure
/// belongs to that run alone), a memory guard and a timeout per run, medians of
/// the repetitions and a fixed flat/grows classification.
/// </summary>
internal static class SuiteRunner
{
    /// <summary>Working set above which a run is stopped (outcome <c>memory-guard</c>).</summary>
    public const long MemoryGuardBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Spike classification: a ratio 1m/100k at or below this is flat. Not a product target.</summary>
    public const double FlatRatio = 1.5;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Runs the suite and writes <c>measurements-&lt;os&gt;.csv</c> and <c>summary-&lt;os&gt;.csv</c>.</summary>
    public static int Run(string datasets, string results, string work, IReadOnlyList<string> sizes, IReadOnlyList<string> candidates, int repetitions, TextWriter log)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
        var profile = Path.Combine(AppContext.BaseDirectory, "profiles", "volume-exact-text.json");
        Directory.CreateDirectory(results);
        var outputDirectory = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;

        var files = new Dictionary<string, (string Path, string Sha256)>(StringComparer.Ordinal);
        foreach (var size in sizes)
        {
            var path = Path.Combine(datasets, $"header-items-v1.volume.{size}.json");
            if (!File.Exists(path))
            {
                log.WriteLine($"dataset missing: {Path.GetFileName(path)}");
                return 1;
            }

            using var stream = File.OpenRead(path);
            files[size] = (path, Convert.ToHexStringLower(SHA256.HashData(stream)));
            log.WriteLine($"dataset {Path.GetFileName(path)} sha256={files[size].Sha256}");
        }

        var measurementsPath = Path.Combine(results, $"measurements-{os}.csv");
        var rows = new List<Measurement>();
        using (var csv = NewWriter(measurementsPath))
        {
            csv.WriteLine(Measurement.Header);
            var runNumber = 0;
            foreach (var size in sizes)
            {
                for (var repetition = 1; repetition <= repetitions; repetition++)
                {
                    var order = candidates.Skip((repetition - 1) % candidates.Count).Concat(candidates.Take((repetition - 1) % candidates.Count));
                    foreach (var candidate in order)
                    {
                        runNumber++;
                        var keep = size == "1m" && repetition == repetitions;
                        var output = Path.Combine(outputDirectory, $"{candidate}-{size}{(keep ? string.Empty : "-r" + repetition.ToString(Inv))}.xlsx");
                        if (File.Exists(output))
                        {
                            File.Delete(output);
                        }

                        var temp = Path.Combine(work, $"tmp-{runNumber.ToString(Inv)}");
                        if (Directory.Exists(temp))
                        {
                            Directory.Delete(temp, recursive: true);
                        }

                        Directory.CreateDirectory(temp);
                        var measurement = RunChild(candidate, files[size].Path, profile, output, temp);
                        measurement.Os = os;
                        measurement.Size = size;
                        measurement.Repetition = repetition;
                        measurement.DatasetSha256 = files[size].Sha256;
                        rows.Add(measurement);
                        csv.WriteLine(measurement.ToCsv());
                        csv.Flush();
                        log.WriteLine($"run {runNumber}: {candidate} {size} r{repetition} {measurement.Outcome} rss={measurement.Result?.ProcessPeakRssMiB.ToString(Inv) ?? "-"} heap={measurement.Result?.ManagedHeapPeakMiB.ToString(Inv) ?? "-"} s={measurement.Result?.ElapsedSeconds.ToString(Inv) ?? "-"}");

                        if (!keep && File.Exists(output))
                        {
                            File.Delete(output);
                        }

                        Directory.Delete(temp, recursive: true);
                    }
                }
            }
        }

        WriteSummary(Path.Combine(results, $"summary-{os}.csv"), rows, sizes, candidates);
        log.WriteLine($"written {Path.GetFileName(measurementsPath)} and summary-{os}.csv");
        return 0;
    }

    private static Measurement RunChild(string candidate, string dataset, string profile, string output, string temp)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(SuiteRunner).Assembly.Location);
        }

        foreach (var argument in new[] { "write", "--candidate", candidate, "--input", dataset, "--columns", profile, "--output", output })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["TMP"] = temp;
        start.Environment["TEMP"] = temp;
        start.Environment["TMPDIR"] = temp;
        start.Environment["XLSX_SPIKE_MEASURE_TEMP"] = "1";

        var lines = new List<string>();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lines)
                {
                    lines.Add(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (errors)
                {
                    errors.Append(e.Data).Append(' ');
                }
            }
        };

        var clock = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        string? guard = null;
        long observedPeak = 0;
        while (!process.WaitForExit(200))
        {
            try
            {
                process.Refresh();
                observedPeak = Math.Max(observedPeak, process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (observedPeak > MemoryGuardBytes)
            {
                guard = "memory-guard";
            }
            else if (clock.Elapsed > Timeout)
            {
                guard = "timeout";
            }

            if (guard is not null)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                break;
            }
        }

        process.WaitForExit();
        RunResult? result = null;
        lock (lines)
        {
            var json = lines.LastOrDefault(l => l.StartsWith('{'));
            if (json is not null)
            {
                result = RunResult.FromJson(json);
            }
        }

        var outcome = guard ?? result?.Outcome ?? "failed";
        return new Measurement
        {
            Candidate = candidate,
            Dataset = Path.GetFileName(dataset),
            Outcome = outcome,
            Result = result,
            ObservedPeakWorkingSetMiB = RunResult.MiB(observedPeak),
            Error = guard is not null ? $"stopped by the suite ({guard})" : result?.ErrorMessage ?? (result is null ? OneLine(errors.ToString()) : null),
        };
    }

    private static void WriteSummary(string path, List<Measurement> rows, IReadOnlyList<string> sizes, IReadOnlyList<string> candidates)
    {
        using var csv = NewWriter(path);
        csv.WriteLine("candidate,size,runs,okRuns,managedHeapPeakMiB,processPeakRssMiB,chromiumTreePeakRssMiB,tempDiskPeakMiB,outputMiB,rowsPerSecond,elapsedSeconds,allocatedMiB,gen2Collections,sourceEnumerations,rssRatio,heapRatio,verdict");
        foreach (var candidate in candidates)
        {
            var bySize = sizes.ToDictionary(s => s, s => rows.Where(r => r.Candidate == candidate && r.Size == s).ToList(), StringComparer.Ordinal);
            var failedAt = sizes.FirstOrDefault(s => bySize[s].Any(r => r.Outcome != "ok"));
            double? Median(string size, Func<RunResult, double?> metric)
            {
                var values = bySize[size].Where(r => r.Outcome == "ok" && r.Result is not null)
                    .Select(r => metric(r.Result!)).Where(v => v is not null).Select(v => v!.Value).Order().ToList();
                if (values.Count == 0)
                {
                    return null;
                }

                return values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
            }

            double? rssRatio = null;
            double? heapRatio = null;
            string verdict;
            if (failedAt is not null)
            {
                verdict = "failed at " + failedAt;
            }
            else if (bySize.ContainsKey("100k") && bySize.ContainsKey("1m"))
            {
                rssRatio = Ratio(Median("1m", r => r.ProcessPeakRssMiB), Median("100k", r => r.ProcessPeakRssMiB));
                heapRatio = Ratio(Median("1m", r => r.ManagedHeapPeakMiB), Median("100k", r => r.ManagedHeapPeakMiB));
                verdict = rssRatio <= FlatRatio && heapRatio <= FlatRatio ? "flat" : "grows";
            }
            else
            {
                verdict = "n/a (needs 100k and 1m)";
            }

            foreach (var size in sizes)
            {
                var values = new[]
                {
                    candidate,
                    size,
                    bySize[size].Count.ToString(Inv),
                    bySize[size].Count(r => r.Outcome == "ok").ToString(Inv),
                    Format(Median(size, r => r.ManagedHeapPeakMiB)),
                    Format(Median(size, r => r.ProcessPeakRssMiB)),
                    "n/a",
                    Format(Median(size, r => r.TempDiskPeakMiB)),
                    Format(Median(size, r => r.OutputMiB)),
                    Format(Median(size, r => Math.Round(r.RowsPerSecond))),
                    Format(Median(size, r => r.ElapsedSeconds)),
                    Format(Median(size, r => r.AllocatedMiB)),
                    Format(Median(size, r => r.Gen2Collections)),
                    Format(Median(size, r => r.SourceEnumerations)),
                    Format(rssRatio is null ? null : Math.Round(rssRatio.Value, 3)),
                    Format(heapRatio is null ? null : Math.Round(heapRatio.Value, 3)),
                    verdict,
                };
                csv.WriteLine(string.Join(',', values));
            }
        }
    }

    private static double? Ratio(double? top, double? bottom) => top is null || bottom is null or 0 ? null : top / bottom;

    private static string Format(double? value) => value?.ToString("0.###", Inv) ?? string.Empty;

    private static StreamWriter NewWriter(string path) => new(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" };

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Replace(',', ';').Trim();

    /// <summary>One line of <c>measurements-&lt;os&gt;.csv</c>.</summary>
    private sealed class Measurement
    {
        public const string Header =
            "date,os,runtime,gcMode,candidate,profile,size,repetition,dataset,datasetSha256,outcome,rowsWritten,sheets,elapsedSeconds,rowsPerSecond,managedHeapPeakMiB,processPeakRssMiB,chromiumTreePeakRssMiB,tempDiskPeakMiB,outputMiB,allocatedMiB,gen2Collections,sourceEnumerations,suiteObservedPeakWorkingSetMiB,error";

        public string Date { get; } = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", Inv);

        public string Os { get; set; } = string.Empty;

        public required string Candidate { get; init; }

        public string Size { get; set; } = string.Empty;

        public int Repetition { get; set; }

        public required string Dataset { get; init; }

        public string DatasetSha256 { get; set; } = string.Empty;

        public required string Outcome { get; init; }

        public RunResult? Result { get; init; }

        public double ObservedPeakWorkingSetMiB { get; init; }

        public string? Error { get; init; }

        public string ToCsv()
        {
            var r = Result;
            var values = new[]
            {
                Date,
                Os,
                Quote(r?.Runtime),
                Quote(r?.GcMode),
                Candidate,
                r?.Profile ?? "volume-exact-text",
                Size,
                Repetition.ToString(Inv),
                Dataset,
                DatasetSha256,
                Outcome,
                r?.RowsWritten.ToString(Inv) ?? string.Empty,
                r?.Sheets.ToString(Inv) ?? string.Empty,
                F(r?.ElapsedSeconds),
                F(r is null ? null : Math.Round(r.RowsPerSecond)),
                F(r?.ManagedHeapPeakMiB),
                F(r?.ProcessPeakRssMiB),
                "n/a",
                F(r?.TempDiskPeakMiB),
                F(r?.OutputMiB),
                F(r?.AllocatedMiB),
                r?.Gen2Collections.ToString(Inv) ?? string.Empty,
                r?.SourceEnumerations.ToString(Inv) ?? string.Empty,
                F(ObservedPeakWorkingSetMiB),
                Quote(Error),
            };
            return string.Join(',', values);
        }

        private static string F(double? value) => value?.ToString("0.###", Inv) ?? string.Empty;

        private static string Quote(string? value) =>
            value is null ? string.Empty : "\"" + value.Replace("\"", "'", StringComparison.Ordinal) + "\"";
    }
}
